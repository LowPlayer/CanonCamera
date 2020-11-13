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
            if (!camera.Init(out String errMsg))
            {
                MessageBox.Show(errMsg);
                return;
            }

            this.Title = camera.CameraName;

            if (!camera.Play(out errMsg))
                MessageBox.Show(errMsg);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            camera.Dispose();
        }

        private void OnStartEvfClick(object sender, RoutedEventArgs e)
        {
            if (!camera.Play(out String errMsg))
                MessageBox.Show(errMsg);
        }

        private void OnEndEvfClick(object sender, RoutedEventArgs e)
        {
            if (!camera.Stop(out String errMsg))
                MessageBox.Show(errMsg);
        }

        private void OnTakePictureClick(object sender, RoutedEventArgs e)
        {
            if (!camera.TakePicture(out String errMsg))
                MessageBox.Show(errMsg);
        }

        private void OnBeginRecordClick(object sender, RoutedEventArgs e)
        {
            if (!camera.BeginRecord(out String errMsg))
                MessageBox.Show(errMsg);
        }

        private void OnEndRecordClick(object sender, RoutedEventArgs e)
        {
            if (!camera.EndRecord(out String errMsg))
                MessageBox.Show(errMsg);
        }
    }
}
