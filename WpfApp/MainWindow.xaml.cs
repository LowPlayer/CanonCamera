using System;
using System.IO;
using System.Windows;

namespace WpfApp
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private Camera camera;

        public MainWindow()
        {
            InitializeComponent();

            camera = new Camera
            {
                ImageSourceChanged = n => { this.img.Source = n; },
                ImageFolder = Path.Combine(Environment.CurrentDirectory, "Images"),
                VideoFolder = Path.Combine(Environment.CurrentDirectory, "Videos"),
                NamingRulesFunc = () => (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds.ToString("0")
            };

            this.Loaded += OnLoaded;
        }

        private void OnLoaded(Object sender, RoutedEventArgs e)
        {
            if (!camera.Init())
            {
                MessageBox.Show("未检测到相机");
                return;
            }

            this.Title = camera.CameraName;

            camera.Play();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            camera.Dispose();
        }

        private void OnStartEvfClick(object sender, RoutedEventArgs e)
        {
            camera.Play();
        }

        private void OnEndEvfClick(object sender, RoutedEventArgs e)
        {
            camera.Stop();
        }

        private void OnTakePictureClick(object sender, RoutedEventArgs e)
        {
            camera.TakePicture();
        }

        private void OnBeginRecordClick(object sender, RoutedEventArgs e)
        {
            camera.BeginRecord();
        }

        private void OnEndRecordClick(object sender, RoutedEventArgs e)
        {
            camera.EndRecord();
        }
    }
}
