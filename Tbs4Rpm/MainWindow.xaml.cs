using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Timers;
using System.Windows.Threading;
using System.IO.Ports;
using System.Diagnostics;
using System.Threading;

namespace Tbs4
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public delegate void UpdateTbsDelegate();

        private struct Sample
        {
            public int Channel;
            public double CycleWidthSeconds;
            public double Min;
            public double Value;
            public double Max;
            public double Delta;

            public bool InitFromString(string data)
            {
                float value;
                string[] values = data.Split(new char[] { ',' });
                if (values.Length != 5)
                {
                    return false;
                }
                if (!int.TryParse(values[0], out Channel))
                {
                    return false;
                }

                if (!float.TryParse(values[1], out value))
                {
                    return false;
                }
                CycleWidthSeconds = value / 1000.0f;
                if (!float.TryParse(values[2], out value))
                {
                    return false;
                }
                Min =  value / 100.0f * -1.0f;
                if (!float.TryParse(values[3], out value))
                {
                    return false;
                }
                Value = value / 100.0f * -1.0f;
                if (!float.TryParse(values[4], out value))
                {
                    return false;
                }
                Max = value / 100.0f * -1.0f;
                if (Max < Min)
                {
                    return false;
                }
                return true;
            }
            public void Add(Sample sample)
            {
                CycleWidthSeconds += sample.CycleWidthSeconds;
                Min += sample.Min;
                Value += sample.Value;
                Max += sample.Max;
                Delta += sample.Delta;
            }
            public void Subtract(Sample sample)
            {
                CycleWidthSeconds -= sample.CycleWidthSeconds;
                Min -= sample.Min;
                Value -= sample.Value;
                Max -= sample.Max;
                Delta -= sample.Delta;
            }
            public void Divide(double divisor)
            {
                CycleWidthSeconds /= divisor;
                Min /= divisor;
                Value /= divisor;
                Max /= divisor;
                Delta /= divisor;
            }
            public void Multiply(double multiplier)
            {
                CycleWidthSeconds *= multiplier;
                Min *= multiplier;
                Value *= multiplier;
                Max *= multiplier;
                Delta *= multiplier;
            }
        }

        private struct SampleSet
        {
            public int Count;
            public Sample[] Channels;

            public bool InitFromString(string data)
            {
                string[] values = data.Split(new char[] { ';' });
                if (!int.TryParse(values[0], out Count))
                {
                    return false;
                }
                if (values.Length != Count + 1)
                {
                    return false;
                }

                Channels = new Sample[Count];
                double minValue = float.MaxValue;
                double maxValue = float.MinValue;
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    if (!Channels[indexChannel].InitFromString(values[indexChannel + 1]))
                    {
                        return false;
                    }
                    // collect the max and min of the values
                    double value = Channels[indexChannel].Value;
                    maxValue = Math.Max(value, maxValue);
                    minValue = Math.Min(value, minValue);
                }
                // calculate the delta  between value and the furthest away min/max value
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    double value = Channels[indexChannel].Value;
                    double diffMax = value - maxValue;
                    double diffMin = value - minValue;
                    if (Math.Abs(diffMax) > Math.Abs(diffMin))
                    {
                        Channels[indexChannel].Delta = diffMax;
                    }
                    else
                    {
                        Channels[indexChannel].Delta = diffMin;
                    }
                }
                return true;
            }
            
            public void Add(SampleSet set)
            {
                Debug.Assert(set.Count == this.Count);
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    Channels[indexChannel].Add(set.Channels[indexChannel]);
                }
            }
            public void Subtract(SampleSet set)
            {
                Debug.Assert(set.Count == this.Count);
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    Channels[indexChannel].Subtract(set.Channels[indexChannel]);
                }
            }
            public void Divide(double divisor)
            {
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    Channels[indexChannel].Divide(divisor);
                }
            }
            public static SampleSet operator /(SampleSet dividend, double divisor)
            {
                SampleSet quotient = dividend;
                quotient.Divide(divisor);
                return quotient;
            }
            public void Multiply(double multiplier)
            {
                for (int indexChannel = 0; indexChannel < Count; indexChannel++)
                {
                    Channels[indexChannel].Multiply(multiplier);
                }
            }
            public static SampleSet operator *(SampleSet multiplicand, double multiplier)
            {
                SampleSet product = multiplicand;
                product.Multiply(multiplier);
                return product;
            }
        }

        private System.Timers.Timer timer;
        private Random rnd;
        private SerialPort comms;
        int isProcessingSample = 0;

#if UseAverage
        bool isSumInitialized = false;
        int indexSamples = 0;
        private SampleSet[] samples = new SampleSet[50];
        private SampleSet samplesSum = new SampleSet();
#endif
        const int CommServiceTimeMs = 10;
        const int CommReadTimeOut = 2;

        private void ResetTbs(string portName)
        {
            comms = new SerialPort(portName, 1200, Parity.None);
            comms.RtsEnable = true;
            comms.DtrEnable = true;

            comms.Open();
            comms.Close();
        }

        private string FindPortName(List<string> portsSkip)
        {
            string[] ports = SerialPort.GetPortNames();

            // find the first port skipping any with single digit
            // or in the portsSkip list
            for (int indexPort = 0; indexPort < ports.GetLength(0); indexPort++)
            {
                string portName = ports[indexPort];
                if (portName.Length > 4) // skip COM5
                {
                    if (!portsSkip.Contains(portName))
                    {
                        return portName;
                    }
                }
            }

            return null;
        }

        private bool ConnectPort(string portName)
        {
            bool found = false;
            comms = new SerialPort(portName, 19200, Parity.None);
            comms.NewLine = "\r\n";
            comms.RtsEnable = true;
            comms.DtrEnable = true;
            comms.ErrorReceived += comms_ErrorReceived;
            comms.ReadTimeout = 2000; // large for initial setup
            try
            {
                comms.Open();
                
                comms.WriteLine("query version");
                string message = comms.ReadLine();
                if (message.StartsWith( "<version="))
                {
                    if (message != "<version=0.16>")
                    {
                        MessageBox.Show("Incompatible TBS Sensor Unit Found", message, MessageBoxButton.OK);
                    }
                    else
                    {
                        found = true;
                    }
                }
            }
            catch (Exception e)
            {
                comms.Close();
            }
            return found;
        }

        public MainWindow()
        {
            InitializeComponent();
            rnd = new Random();

            List<string> portsSkip = new List<string>();
            bool isConnected = false;

            while (!isConnected)
            {
                string portName = FindPortName(portsSkip);
                if (portName == null)
                {
                    MessageBox.Show("Make sure the TBS unit is plugged in before starting this application.", "TBS Sensor Unit not found", MessageBoxButton.OK);
                    App.Current.Shutdown();
                    break;
                }
                isConnected = ConnectPort(portName);
                if (!isConnected)
                {
                    portsSkip.Add(portName);
                }
            }

            if (isConnected)
            {
                // use normal timeout
                comms.ReadTimeout = CommReadTimeOut;

                StartIdle();
            }
        }

        private void comms_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            MessageBox.Show("<set smart description here>", "Communications Error", MessageBoxButton.OK);
            App.Current.Shutdown();
        }

        void timer_ElapsedIdle(object sender, ElapsedEventArgs args)
        {
            string data;

            try
            {
                data = comms.ReadLine();
            }
            catch (Exception e)
            {
                data = "";
            }

            if (data == "<idle0>")
            {
                this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new UpdateTbsDelegate(
                        delegate()
                        {
                            this.tbsStatusLabel.Content = " ALIVE ";
                            this.tbsStatusLabel.FontWeight = FontWeights.Bold;
                        }));
                
            }
            else if (data == "<idle1>")
            {
                this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new UpdateTbsDelegate(
                        delegate()
                        {
                            this.tbsStatusLabel.Content = " ALIVE ";
                            this.tbsStatusLabel.FontWeight = FontWeights.Normal;
                        }));
            }
        }

        void timer_ElapsedCalibration(object sender, ElapsedEventArgs args)
        {
            string data;

            try
            {
                data = comms.ReadLine();
            }
            catch (Exception e)
            {
                data = "";
            }

            if (data == "<calibrating...>")
            {
            }
            else if (data == "<...calibrated>")
            {
                this.timer.Stop();
                this.timer.Elapsed -= timer_ElapsedCalibration;

                StartIdle();

                this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new UpdateTbsDelegate(
                        delegate()
                        {
                            this.btnCalibrate.IsEnabled = true;
                            this.btnStart.IsEnabled = true;
                            this.btnStop.IsEnabled = false;
                        }));
            }
        }

        void timer_ElapsedSampling(object sender, ElapsedEventArgs args)
        {
            string data;

            // protect from reentrancy
            if (1 == Interlocked.CompareExchange( ref isProcessingSample, 1, 0))
            {
                return;
            }
            
            try
            {
                data = comms.ReadLine();
            }
            catch (Exception e)
            {
                data = "";
            }

            if (data.StartsWith("<"))
            {
                if (data == "<overrun>")
                {
                    MessageBox.Show("Sampling Overrun", "Sampling Error", MessageBoxButton.OK);
                }
                else if (data == "<stopped>")
                {
                    this.timer.Stop();
                    this.timer.Elapsed -= timer_ElapsedSampling;

                    StartIdle();

                    this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new UpdateTbsDelegate(
                            delegate()
                            {
                                this.btnCalibrate.IsEnabled = true;
                                this.btnStart.IsEnabled = true;
                                this.btnStop.IsEnabled = false;
                            }));
                }
                else if (data == "<running>")
                {
                    this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            new UpdateTbsDelegate(
                            delegate()
                            {
                                this.btnStop.IsEnabled = true;
                            }));
                }
            }
            else if (data.StartsWith("$") && data.EndsWith("\x03"))
            {
                // 4;0,50,1005,912,805;1,50,1005,912,805;2,50,1005,912,805;3,50,1005,912,805;1678
                bool failedConvert = true;
                SampleSet sample = new SampleSet();
                data = data.TrimEnd('\x03');
                
                // calc a check sum
                int sum = 0; // we calculate
                int checkSum = 0; // from the data
                int count = data.LastIndexOf(';');
                string checkSumText = data.Substring(count + 1);
                if (int.TryParse(checkSumText, out checkSum))
                {
                    for (int index = 0; index < count; index++)
                    {
                        sum += data[index];
                    }

                    if (sum == checkSum)
                    {
                        // remove checksum text and $ prefix
                        data = data.Substring(1, count-1);
                        // check sum passed
                        if (sample.InitFromString(data))
                        {
                            failedConvert = false;
                            ProcessSample(sample);
                        }
                    }
                }
                if (failedConvert)
                {
                    comms.DiscardInBuffer();
                }
            }
            isProcessingSample = 0;
        }

        private void StartIdle()
        {
            comms.DiscardInBuffer();
            comms.DiscardOutBuffer();

            this.timer = new System.Timers.Timer(CommServiceTimeMs);
            this.timer.Elapsed += timer_ElapsedIdle;
            this.timer.AutoReset = true;
            this.timer.Start();
        }

        private void StartCalibration()
        {
            comms.DiscardInBuffer();
            comms.DiscardOutBuffer();
            comms.WriteLine("calibrate");

            this.timer.Stop();
            
            this.timer = new System.Timers.Timer(CommServiceTimeMs);
            this.timer.Elapsed += timer_ElapsedCalibration;
            this.timer.AutoReset = true;
            this.timer.Start();
        }

        private void StartSampling()
        {
#if UseAverage
            isSumInitialized = false;
#endif
            comms.DiscardInBuffer();
            comms.DiscardOutBuffer();
            comms.WriteLine("start");

            this.timer.Stop();

            this.timer = new System.Timers.Timer(CommServiceTimeMs);
            this.timer.Elapsed += timer_ElapsedSampling;
            this.timer.AutoReset = true;
            this.timer.Start();
        }

        private void StopSampling()
        {
            comms.DiscardOutBuffer();
            comms.WriteLine("stop");
        }

        private void Ping()
        {
            comms.DiscardOutBuffer();
            comms.WriteLine("");
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            DisableButtonsForStateChange();
            StopSampling();
        }

        private void btnCalibrate_Click(object sender, RoutedEventArgs e)
        {
            DisableButtonsForStateChange();
            StartCalibration();
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            DisableButtonsForStateChange();
            StartSampling();
        }

        private void DisableButtonsForStateChange()
        {
            this.btnCalibrate.IsEnabled = false;
            this.btnStart.IsEnabled = false;
            this.btnStop.IsEnabled = false;
        }

        private void UpdateGraphBar(Sample sample,
                Sample averagedSample,
                Label labelValue,
                Label labelDelta,
                Label labelRpm,
                Canvas host,
                Rectangle averageBar,
                Rectangle valueBar, 
                Rectangle minMaxBar)
        {
            double value = averagedSample.Value;
            double min = sample.Min;
            double pixelPerKpa = host.ActualHeight / 50.0;
            double range = sample.Max - min;
            labelValue.Content = String.Format("{0:F02} kPa", value);
            labelDelta.Content = String.Format("{0:F02} kPa", averagedSample.Delta);
            labelRpm.Content = String.Format("{0:F0} rpm", 60.0 / averagedSample.CycleWidthSeconds);
            averageBar.SetValue(Canvas.TopProperty, host.ActualHeight +
                    (pixelPerKpa * value) -
                    averageBar.ActualHeight * 0.5); // centered
            valueBar.SetValue(Canvas.TopProperty, host.ActualHeight +
                    (pixelPerKpa * sample.Value) - 
                    valueBar.ActualHeight * 0.5); // centered
            minMaxBar.Height = pixelPerKpa * range;
            minMaxBar.SetValue(Canvas.TopProperty, host.ActualHeight + (pixelPerKpa * min));
        }

        private void ProcessSample(SampleSet sample)
        {
#if UseAverage
            if (!isSumInitialized)
            {
                isSumInitialized = true;
                // init running averaging store
                indexSamples = 0;
                samplesSum = sample * samples.Length;
                for (int indexSample = 0; indexSample < samples.Length; indexSample++)
                {
                    samples[indexSample] = sample;
                }
            }
            else
            {
                samplesSum.Subtract(samples[indexSamples]);
                samplesSum.Add(sample);
                samples[indexSamples] = sample;
            }

            SampleSet averageSample = samplesSum / samples.Length;
#endif
            this.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new UpdateTbsDelegate(
                    delegate()
                    {
                        for (int indexChannel = 0; indexChannel < sample.Channels.Length; indexChannel++)
                        {
                            switch (sample.Channels[indexChannel].Channel)
                            {
                                case 0:
                                    UpdateGraphBar(sample.Channels[indexChannel],
                                            sample.Channels[indexChannel],
                                            tbs1Label,
                                            tbs1DeltaLabel,
                                            tbs1RpmLabel,
                                            tbs1,
                                            tbs1Average,
                                            tbs1Reading,
                                            tbs1MinMax);
                                    break;
                                case 1:
                                    UpdateGraphBar(sample.Channels[indexChannel],
                                            sample.Channels[indexChannel],
                                            tbs2Label,
                                            tbs2DeltaLabel,
                                            tbs2RpmLabel,
                                            tbs2,
                                            tbs2Average,
                                            tbs2Reading,
                                            tbs2MinMax);
                                    break;
                                case 2:
                                    UpdateGraphBar(sample.Channels[indexChannel],
                                            sample.Channels[indexChannel],
                                            tbs3Label,
                                            tbs3DeltaLabel,
                                            tbs3RpmLabel,
                                            tbs3,
                                            tbs3Average,
                                            tbs3Reading,
                                            tbs3MinMax);
                                    break;
                                case 3:
                                    UpdateGraphBar(sample.Channels[indexChannel],
                                            sample.Channels[indexChannel],
                                            tbs4Label,
                                            tbs4DeltaLabel,
                                            tbs4RpmLabel,
                                            tbs4,
                                            tbs4Average,
                                            tbs4Reading,
                                            tbs4MinMax);
                                    break;
                            }
                        }

                    }));
        }
    }
}

