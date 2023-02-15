using System;
using System.Diagnostics;
using System.Security.Cryptography;

using SLAB_HID_TO_SMBUS;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace HTU21D_CP2112 {
    public partial class MainForm : Form {
        public MainForm() {
            InitializeComponent();
            notifyIcon.Icon = this.Icon;
        }

        private SensorHTU21D sensor = new SensorHTU21D();

        private void refreshingTimer_Tick(object sender, EventArgs eventArgs) {
            bool error = false;

            if (!sensor.Connected) {
                try {
                    sensor.Connect();
                    Debug.Print("Connection established");
                } catch (Exception e) {
                    Debug.Print(e.Message);
                    error = true;
                }
            }

            float temperature = 0;
            float humidity = 0;

            if (!error && sensor.Read(ref temperature, ref humidity)) {
                lblTemperature.Text = string.Format("{0:0.0} °C", temperature);
                lblHumidity.Text = string.Format("{0:0} %", humidity);
                notifyIcon.Text = string.Format("Temperature: {0}\r\nHumidity: {1}", lblTemperature.Text, lblHumidity.Text);
            } else {
                lblTemperature.Text = "Oops!";
                lblHumidity.Text = "";
                notifyIcon.Text = "(ERROR) HTU21D Sensor";
            }
        }
    }

    public class SensorHTU21D {
        protected IntPtr hAdapter;
        protected byte deviceAddress;

        protected bool ReadRegister(byte register, byte[] buffer, byte readSize) {
            try {
                byte status = 0;
                byte[] readbuff = new byte[61];

                if (CP2112_DLL.HidSmbus_WriteRequest(hAdapter, deviceAddress, new byte[] { register }, 1) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                    throw new Exception("Failed to start conversion");
                }

                // Wait for I/O completed.

                if (CP2112_DLL.HidSmbus_TransferStatusRequest(hAdapter) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                    throw new Exception("I/O error (0)");
                }

                status = 0;
                byte detailedStatus = 0;

                do {
                    ushort numRetries = 0;
                    ushort bytesRead = 0;

                    if (CP2112_DLL.HidSmbus_GetTransferStatusResponse(hAdapter, ref status, ref detailedStatus, ref numRetries, ref bytesRead) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                        throw new Exception("I/O error (1)");
                    }
                } while (status == CP2112_DLL.HID_SMBUS_S0_BUSY);

                if (status != CP2112_DLL.HID_SMBUS_S0_COMPLETE) {
                    if (status != CP2112_DLL.HID_SMBUS_S0_ERROR || detailedStatus != CP2112_DLL.HID_SMBUS_S1_ERROR_SUCCESS_AFTER_RETRY) {
                        throw new Exception(String.Format("Communication with target device has failed (code {0})", detailedStatus));
                    }
                }

                Thread.Sleep(50);

                if (CP2112_DLL.HidSmbus_ReadRequest(hAdapter, deviceAddress, readSize) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                    throw new Exception("Failed to start read operation (0)");
                }

                if (CP2112_DLL.HidSmbus_ForceReadResponse(hAdapter, readSize) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                    throw new Exception("Failed to start read operation (1)");
                }

                for (byte totBytesRead = 0; totBytesRead < readSize;) {
                    byte bytesRead = 0;

                    status = 0;
                    int code = CP2112_DLL.HidSmbus_GetReadResponse(hAdapter, ref status, readbuff, 61, ref bytesRead);

                    if (code != CP2112_DLL.HID_SMBUS_SUCCESS) {
                        throw new Exception("Failed to read data");
                    }

                    for (int r = 0; r < bytesRead; r++) {
                        int index = totBytesRead + r;
                        if ((0 <= index) && (index < readSize))
                            buffer[index] = readbuff[r];
                    }
                    totBytesRead += bytesRead;
                }
            } catch (Exception e) {
                Debug.Print(String.Format("Failed to read: {0}", e.Message));
                return false;
            }

            return true;
        }

        public static float ToTemperature(byte[] rawData) {
            int value = (rawData[0] << 8) | rawData[1]; 
            return -46.85f + 175.72f * value / 65535.0f;
        }

        public static float ToHumidity(byte[] rawData) {
            int value = (rawData[0] << 8) | rawData[1];
            return -6.0f + 125.0f * value / 65535.0f;
        }

        public bool Read(ref float temperature, ref float humidity) {
            if (!Connected) {
                throw new Exception("Not connected");
            }

            const int DATA_SIZE = 2;

            byte[] rawTemperature = new byte[DATA_SIZE];
            byte[] rawHumidity = new byte[DATA_SIZE];

            if (!ReadRegister(0xF3, rawTemperature, DATA_SIZE)) {
                return false;
            }

            if (0 != (rawTemperature[1] & 0x02)) {
                Debug.Print("Expected bit 1 cleared");
                return false;
            }

            if (!ReadRegister(0xF5, rawHumidity, DATA_SIZE)) {
                return false;
            }

            if (0 == (rawHumidity[1] & 0x02)) {
                Debug.Print("Expected bit 1 set");
                return false;
            }

            temperature = ToTemperature(rawTemperature);
            humidity = ToHumidity(rawHumidity);

            Debug.Print(string.Format("{0}: Done reading measurements", DateTime.Now));

            return true;
        }

        public void Connect(byte deviceAddress = 0x40 << 1, ushort usbVID = 0x10C4, ushort usbPID = 0xEA90) {
            if (Connected) {
                return;
            }

            uint numDevices = 0;

            if (CP2112_DLL.HidSmbus_GetNumDevices(ref numDevices, usbVID, usbPID) == CP2112_DLL.HID_SMBUS_SUCCESS) {
                // Devices have been enumerated.

                if (numDevices == 0) {
                    throw new Exception("No adapter found");
                } else if (numDevices >= 2) {
                    throw new Exception("More than 2 adapters found. Aborting.");
                }

                // Try to open a device and configure it.
                
                if (CP2112_DLL.HidSmbus_Open(ref hAdapter, 0, usbVID, usbPID) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                    throw new Exception("Failed to connect to an adapter");
                }

                try {
                    if (CP2112_DLL.HidSmbus_SetSmbusConfig(hAdapter, 100000, 0x02, 0, 1000, 1000, 1000, 2) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                        throw new Exception("Failed to configure the adapter");
                    }

                    // Write a default value to the user register of the sensor (test device is presented on the bus).

                    if (CP2112_DLL.HidSmbus_WriteRequest(hAdapter, deviceAddress, new byte[] { 0xE6, 0x02 }, 2) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                        throw new Exception("Failed to detect/configure target device");
                    }

                    // Wait for I/O completed.

                    if (CP2112_DLL.HidSmbus_TransferStatusRequest(hAdapter) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                        throw new Exception("I/O error (0)");
                    }

                    byte status = 0;
                    byte detailedStatus = 0;

                    do { 
                        ushort numRetries = 0;
                        ushort bytesRead = 0;

                        if (CP2112_DLL.HidSmbus_GetTransferStatusResponse(hAdapter, ref status, ref detailedStatus, ref numRetries, ref bytesRead) != CP2112_DLL.HID_SMBUS_SUCCESS) {
                            throw new Exception("I/O error (1)");
                        }
                    } while (status == CP2112_DLL.HID_SMBUS_S0_BUSY);

                    if (status != CP2112_DLL.HID_SMBUS_S0_COMPLETE) {
                        if (status != CP2112_DLL.HID_SMBUS_S0_ERROR || detailedStatus != CP2112_DLL.HID_SMBUS_S1_ERROR_SUCCESS_AFTER_RETRY) {
                            throw new Exception(String.Format("Communication with target device has failed (code {0})", detailedStatus));
                        }
                    }

                    this.deviceAddress = deviceAddress;
                    Connected = true;
                } catch (Exception e) {
                    CP2112_DLL.HidSmbus_Close(hAdapter);
                    throw e;
                }
            } else {
                throw new Exception("Failed to enumerate adapters");
            }
        }

        public void Close() {
            if (Connected) {
                CP2112_DLL.HidSmbus_Close(hAdapter);
                Connected = false;
                Debug.Print("Connection closed");
            }
        }

        public bool Connected { get; private set; } = false;
    }
}