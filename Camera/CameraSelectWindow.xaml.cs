using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Lanzhou_v1._0.Camera
{
    public partial class CameraSelectWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public ObservableCollection<CameraDeviceDescriptor> Devices { get; } = new();

        private CameraDeviceDescriptor? _selectedDevice;
        public CameraDeviceDescriptor? SelectedDevice
        {
            get => _selectedDevice;
            set { _selectedDevice = value; OnPropertyChanged(); }
        }

        public CameraDeviceDescriptor? Result => SelectedDevice;

        public CameraSelectWindow()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += (_, __) => Refresh();
        }

        private void Refresh()
        {
            try
            {
                Devices.Clear();
                foreach (var d in EbusCameraService.ScanDevices())
                    Devices.Add(d);

                if (Devices.Count > 0 && SelectedDevice == null)
                    SelectedDevice = Devices[0];
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "扫描相机失败：" + ex.Message, "相机", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDevice == null)
            {
                MessageBox.Show(this, "请先选择一个相机设备。", "相机", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
