using System;
using System.Windows.Media;

namespace WpfApp
{
    /// <summary>
    /// 相机接口
    /// </summary>
    public interface ICamera : IDisposable
    {
        /// <summary>
        /// 初始化
        /// </summary>
        /// <returns></returns>
        Boolean Init(out String errMsg);

        /// <summary>
        /// 开始运行
        /// </summary>
        /// <returns></returns>
        Boolean Play(out String errMsg);

        /// <summary>
        /// 停止运行
        /// </summary>
        /// <returns></returns>
        Boolean Stop(out String errMsg);

        /// <summary>
        /// 开始录像
        /// </summary>
        /// <returns></returns>
        Boolean BeginRecord(out String errMsg);

        /// <summary>
        /// 停止录像
        /// </summary>
        /// <returns></returns>
        Boolean EndRecord(out String errMsg);

        /// <summary>
        /// 拍照
        /// </summary>
        /// <returns></returns>
        Boolean TakePicture(out String errMsg);

        /// <summary>
        /// 图像源改变事件回调通知
        /// </summary>
        Action<ImageSource> ImageSourceChanged { get; set; }

        /// <summary>
        /// 相机名称
        /// </summary>
        String CameraName { get; }
    }
}
