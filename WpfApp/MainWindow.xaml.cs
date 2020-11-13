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
                ImageSourceChanged = n => { this.img.Source = n; },     // 更新图像源
                ImageFolder = Path.Combine(Environment.CurrentDirectory, "Images"),     // 图像保存路径
                VideoFolder = Path.Combine(Environment.CurrentDirectory, "Videos"),     // 录像保存路径
                NamingRulesFunc = () => (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds.ToString("0")       // 新文件命名方式
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

            // 获取相机名称
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
