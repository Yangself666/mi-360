﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;
using mi360.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace mi360
{
    class MiGamepad : IDisposable
    {
        private static readonly Xbox360Button[][] HatSwitches = {
            new [] { Xbox360Button.Up },
            new [] { Xbox360Button.Up, Xbox360Button.Right },
            new [] { Xbox360Button.Right },
            new [] { Xbox360Button.Right, Xbox360Button.Down },
            new [] { Xbox360Button.Down },
            new [] { Xbox360Button.Down, Xbox360Button.Left },
            new [] { Xbox360Button.Left },
            new [] { Xbox360Button.Left, Xbox360Button.Up },
        };

        public event EventHandler Started;
        public event EventHandler Ended;

        private readonly HidDevice _Device;
        private readonly IXbox360Controller _Target;
        private readonly Thread _InputThread;
        private readonly CancellationTokenSource _CTS;

        public MiGamepad(string device, ViGEmClient client)
        {
            _Device = HidDevices.GetDevice(device);
            _Device.MonitorDeviceEvents = false;

            _Target = client.CreateXbox360Controller();
            _Target.AutoSubmitReport = false;
            _Target.FeedbackReceived += Target_OnFeedbackReceived;

            // TODO mark the threads as background?
            _InputThread = new Thread(DeviceWorker);

            _CTS = new CancellationTokenSource();

            LedNumber = 0xFF;
        }

        #region Properties

        public HidDevice Device => _Device;

        public ushort LedNumber { get; private set; }

        public ushort BatteryLevel { get; private set; }

        public bool IsActive => _InputThread.IsAlive;

        #endregion

        #region Methods

        public void Dispose()
        {
            if (_InputThread.IsAlive)
                Stop();

            _Device.Dispose();
            _CTS.Dispose();
        }

        public void Start()
        {
            _InputThread.Start();
        }

        public void  Stop()
        {
            if (_CTS.IsCancellationRequested)
                return;

            _CTS.Cancel();
            _InputThread.Join();
        }

        private void DeviceWorker()
        {
            Console.WriteLine("Starting worker thread for {0}", _Device.ToString());

            DeviceStateManager.DisableReEnableDevice(_Device.DevicePath);

            // Open HID device to read input from the gamepad
            _Device.OpenDevice(DeviceMode.Overlapped, DeviceMode.Overlapped, ShareMode.Exclusive);

            // Init Xiaomi Gamepad vibration
            _Device.WriteFeatureData(new byte[] {0x20, 0x00, 0x00});

            // Connect the virtual Xbox360 gamepad
            try
            {
                _Target.Connect();
            }
            catch (VigemAlreadyConnectedException e)
            {
                _Target.Disconnect();
                _Target.Connect();
            }

            Started?.Invoke(this, EventArgs.Empty);

            HidReport hidReport;

            while (!_CTS.Token.IsCancellationRequested)
            {
                // Is device has been closed, exit the loop
                if (!_Device.IsOpen)
                    break;

                // Otherwise read a report
                hidReport = _Device.ReadReport(1000);

                if (hidReport.ReadStatus == HidDeviceData.ReadStatus.WaitTimedOut)
                    continue;
                else if (hidReport.ReadStatus != HidDeviceData.ReadStatus.Success)
                {
                    Console.WriteLine("Device {0}: error while reading HID report, {1}", _Device.ToString(), hidReport.ReadStatus.ToString());
                    break;
                }

                var data = hidReport.Data;

                /*
                [0]  Buttons state, 1 bit per button
                [1]  Buttons state, 1 bit per button
                [2]  0x00
                [3]  D-Pad
                [4]  Left thumb, X axis
                [5]  Left thumb, Y axis
                [6]  Right thumb, X axis
                [7]  Right thumb, Y axis
                [8]  0x00
                [9]  0x00
                [10] L trigger
                [11] R trigger
                [12] Accelerometer axis 1
                [13] Accelerometer axis 1
                [14] Accelerometer axis 2
                [15] Accelerometer axis 2
                [16] Accelerometer axis 3
                [17] Accelerometer axis 3
                [18] Battery level
                [19] MI button
                 */

                lock (_Target)
                {
                    _Target.SetButtonState(Xbox360Button.A, GetBit(data[0], 0));
                    _Target.SetButtonState(Xbox360Button.B, GetBit(data[0], 1));
                    _Target.SetButtonState(Xbox360Button.X, GetBit(data[0], 3));
                    _Target.SetButtonState(Xbox360Button.Y, GetBit(data[0], 4));
                    _Target.SetButtonState(Xbox360Button.LeftShoulder, GetBit(data[0], 6));
                    _Target.SetButtonState(Xbox360Button.RightShoulder, GetBit(data[0], 7));

                    _Target.SetButtonState(Xbox360Button.Back, GetBit(data[1], 2));
                    _Target.SetButtonState(Xbox360Button.Start, GetBit(data[1], 3));
                    _Target.SetButtonState(Xbox360Button.LeftThumb, GetBit(data[1], 5));
                    _Target.SetButtonState(Xbox360Button.RightThumb, GetBit(data[1], 6));

                    // Reset Hat switch status, as is set to 15 (all directions set, impossible state)
                    _Target.SetButtonState(Xbox360Button.Up, false);
                    _Target.SetButtonState(Xbox360Button.Left, false);
                    _Target.SetButtonState(Xbox360Button.Down, false);
                    _Target.SetButtonState(Xbox360Button.Right, false);

                    if (data[3] < 8)
                    {
                        var btns = HatSwitches[data[3]];
                        // Hat Switch is a number from 0 to 7, where 0 is Up, 1 is Up-Left, etc.
                        foreach (var b in btns)
                            _Target.SetButtonState(b, true);
                    }

                    // Analog axis
                    _Target.SetAxisValue(Xbox360Axis.LeftThumbX, MapAnalog(data[4]));
                    _Target.SetAxisValue(Xbox360Axis.LeftThumbY, MapAnalog(data[5], true));
                    _Target.SetAxisValue(Xbox360Axis.RightThumbX, MapAnalog(data[6]));
                    _Target.SetAxisValue(Xbox360Axis.RightThumbY, MapAnalog(data[7], true));

                    // Triggers
                    _Target.SetSliderValue(Xbox360Slider.LeftTrigger, data[10]);
                    _Target.SetSliderValue(Xbox360Slider.RightTrigger, data[11]);

                    // Logo ("home") button
                    if (GetBit(data[19], 0))
                    {
                        _Target.SetButtonState(Xbox360Button.Guide, true);
                        Task.Delay(200).ContinueWith(DelayedReleaseGuideButton);
                    }

                    // Update battery level
                    BatteryLevel = data[18];

                    _Target.SubmitReport();
                }

            }

            // Disconnect the virtual Xbox360 gamepad
            // Let Dispose handle that, otherwise it will rise a NotPluggedIn exception
            _Target.Disconnect();

            // Close the HID device
            _Device.CloseDevice();

            DeviceStateManager.DisableReEnableDevice(_Device.DevicePath);

            Console.WriteLine("Exiting worker thread for {0}", _Device.ToString());
            Ended?.Invoke(this, EventArgs.Empty);
        }

        private bool GetBit(byte b, int bit)
        {
            return ((b >> bit) & 1) != 0;
        }

        private short MapAnalog(byte value, bool invert = false)
        {
            // Value has value in 0-255

            // Clip it in range -127;127
            var centered = Math.Max(-127, value - 128);

            if (invert)
                centered = -centered;

            return (short)(32767 * centered / 127);
        }

        private void DelayedReleaseGuideButton(Task t)
        {
            lock (_Target)
            {
                _Target.SetButtonState(Xbox360Button.Guide, false);
                _Target.SubmitReport();
            }
        }

        #endregion

        #region Event Handlers

        private void Target_OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            byte[] data = { 0x20, e.SmallMotor, e.LargeMotor };

            Task.Run(() =>
            {
                if (_Device.IsOpen)
                    _Device.WriteFeatureData(data);
            });

            if (LedNumber != e.LedNumber)
            {
                LedNumber = e.LedNumber;
                // TODO raise event here
            }
        }

        #endregion

    }
}
