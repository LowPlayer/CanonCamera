using Accord.Video.FFMPEG;
using EDSDKLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpfApp
{
    public sealed class Camera : ICamera
    {
        static Camera()
        {
            cameraAddedHandler = new EDSDK.EdsCameraAddedHandler(OnCameraAdded);
            objectEventHandler = new EDSDK.EdsObjectEventHandler(OnObjectEvent);
            propertyEventHandler = new EDSDK.EdsPropertyEventHandler(OnPropertyEvent);
            stateEventHandler = new EDSDK.EdsStateEventHandler(OnStateEvent);
        }

        public Camera()
        {
            handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));   // 创建当前对象GC句柄
        }

        #region 公开方法

        public Boolean Init(out String errMsg)
        {
            errMsg = null;

            lock (sdkLock)
            {
                var err = InitCamera();     // 初始化相机
                var ret = err == EDSDK.EDS_ERR_OK;

                if (!ret)
                {
                    errMsg = "未检测到相机，错误代码：" + err;
                    Close();    // 关闭相机
                }

                return ret;
            }
        }

        public Boolean Play(out String errMsg)
        {
            errMsg = null;

            if (camera == IntPtr.Zero)
            {
                if (!Init(out errMsg))
                    return false;
                else
                    Thread.Sleep(500);
            }

            if ((EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
                return true;

            UInt32 err = EDSDK.EDS_ERR_OK;

            lock (sdkLock)
            {
                // 不允许设置AE模式转盘
                //if (AEMode != EDSDK.AEMode_Tv)
                //    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_Mode, 0, sizeof(UInt32), EDSDK.AEMode_Tv);

                // 开启实时取景
                if (err == EDSDK.EDS_ERR_OK && (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) == 0)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, deviceSize, EvfOutputDevice | EDSDK.EvfOutputDevice_PC);
            }

            var ret = err == EDSDK.EDS_ERR_OK;

            if (ret)
            {
                thread_evf = new Thread(ReadEvf) { IsBackground = true };
                thread_evf.SetApartmentState(ApartmentState.STA);
                thread_evf.Start();
            }
            else
                errMsg = "开启实时图像模式失败，错误代码：" + err;

            return ret;
        }

        public Boolean Stop(out String errMsg)
        {
            errMsg = null;

            if (camera == IntPtr.Zero || (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) == 0)
                return true;

            var err = EDSDK.EDS_ERR_OK;

            // 停止实时取景
            lock (sdkLock)
            {
                if (DepthOfFieldPreview != EDSDK.EvfDepthOfFieldPreview_OFF)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_DepthOfFieldPreview, 0, sizeof(UInt32), EDSDK.EvfDepthOfFieldPreview_OFF);

                if (err == EDSDK.EDS_ERR_OK && (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, deviceSize, EvfOutputDevice & ~EDSDK.EvfOutputDevice_PC);
            }

            if (err != EDSDK.EDS_ERR_OK)
                errMsg = "关闭实时图像模式失败，错误代码：" + err;

            return err == EDSDK.EDS_ERR_OK;
        }

        public Boolean TakePicture(out String errMsg)
        {
            errMsg = null;

            if (camera == IntPtr.Zero)
            {
                errMsg = "未检测到相机";
                return false;
            }

            lock (sdkLock)
            {
                // 存储到计算机
                var err = SaveToHost();

                if (err == EDSDK.EDS_ERR_OK)
                {
                    err = EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_PressShutterButton, (Int32)EDSDK.EdsShutterButton.CameraCommand_ShutterButton_Completely);   // 按下拍摄按钮

                    if (err == EDSDK.EDS_ERR_OK)
                        err = EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_PressShutterButton, (Int32)EDSDK.EdsShutterButton.CameraCommand_ShutterButton_OFF);      // 弹起拍摄按钮
                }

                if (err != EDSDK.EDS_ERR_OK)
                    errMsg = "拍照失败，错误代码：" + err;

                return err == EDSDK.EDS_ERR_OK;
            }
        }

        public Boolean BeginRecord(out String errMsg)
        {
            errMsg = null;

            if (camera == IntPtr.Zero)
            {
                errMsg = "未检测到相机";
                return false;
            }

            if (videoFileWriter != null)
                return true;

            if ((EvfOutputDevice & EDSDK.EvfOutputDevice_PC) == 0 && !Play(out errMsg))
                return false;

            videoFileWriter = new VideoFileWriter();
            stopwatch = new Stopwatch();

            return true;
        }

        public Boolean EndRecord(out String errMsg)
        {
            errMsg = null;

            if (camera == IntPtr.Zero)
            {
                errMsg = "未检测到相机";
                return false;
            }

            if (videoFileWriter == null)
                return true;

            lock (videoFileWriter)
            {
                videoFileWriter.Close();
                videoFileWriter = null;
                stopwatch.Stop();
                stopwatch = null;
            }

            return true;
        }

        public void Dispose()
        {
            Close(true);
        }

        #endregion

        #region 私有方法

        private UInt32 GetFirstCamera(out IntPtr camera)
        {
            camera = IntPtr.Zero;

            // 获取相机列表对象
            var err = EDSDK.EdsGetCameraList(out IntPtr cameraList);

            if (err == EDSDK.EDS_ERR_OK)
            {
                err = EDSDK.EdsGetChildCount(cameraList, out Int32 count);

                if (err == EDSDK.EDS_ERR_OK && count > 0)
                {
                    err = EDSDK.EdsGetChildAtIndex(cameraList, 0, out camera);

                    // 释放相机列表对象
                    EDSDK.EdsRelease(cameraList);
                    cameraList = IntPtr.Zero;

                    return err;
                }
            }

            if (cameraList != IntPtr.Zero)
                EDSDK.EdsRelease(cameraList);

            return EDSDK.EDS_ERR_DEVICE_NOT_FOUND;
        }

        private UInt32 InitCamera()
        {
            var err = EDSDK.EDS_ERR_OK;

            if (!isSDKLoaded)
            {
                err = EDSDK.EdsInitializeSDK();     // 初始化SDK

                if (err != EDSDK.EDS_ERR_OK)
                    return err;

                isSDKLoaded = true;
            }

            err = GetFirstCamera(out camera);       // 获取相机对象

            if (err == EDSDK.EDS_ERR_OK)
            {
                // 注册回调函数
                err = EDSDK.EdsSetObjectEventHandler(camera, EDSDK.ObjectEvent_All, objectEventHandler, handle);

                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsSetPropertyEventHandler(camera, EDSDK.PropertyEvent_All, propertyEventHandler, handle);

                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsSetCameraStateEventHandler(camera, EDSDK.StateEvent_All, stateEventHandler, handle);

                // 打开会话
                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsOpenSession(camera);

                if (err == EDSDK.EDS_ERR_OK)
                    isSessionOpened = true;

                // 获取相机名称
                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_ProductName, 0, out cameraName);

                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsGetPropertySize(camera, EDSDK.PropID_Evf_OutputDevice, 0, out _, out deviceSize);

                // 保存到计算机
                if (err == EDSDK.EDS_ERR_OK)
                    err = SaveToHost();


                if (err == EDSDK.EDS_ERR_OK)
                {
                    // 设置自动曝光
                    if (ISOSpeed != 0)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_ISOSpeed, 0, sizeof(UInt32), 0);

                    // 设置拍摄图片质量
                    if (ImageQualityDesc != null)
                        SetImageQualityJpegOnly();

                    // 设置曝光补偿+3
                    if (ExposureCompensation != 0x18)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_ExposureCompensation, 0, sizeof(UInt32), 0x18);

                    // 设置白平衡；自动：环境优先
                    if (ExposureCompensation != 0)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_WhiteBalance, 0, sizeof(UInt32), 0);

                    // 设置测光模式：点测光
                    if (MeteringMode != 0)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_MeteringMode, 0, sizeof(UInt32), 0);

                    // 设置单拍模式
                    if (DriveMode != 0)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_DriveMode, 0, sizeof(UInt32), 0);

                    // 设置快门速度
                    if (Tv != 0x60)
                        EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Tv, 0, sizeof(UInt32), 0x60);
                }
            }

            return err;
        }

        private void ReadEvf()
        {
            // 等待实时图像传输开启
            SpinWait.SpinUntil(() => (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0, 5000);

            IntPtr stream = IntPtr.Zero;
            IntPtr evfImage = IntPtr.Zero;
            IntPtr evfStream = IntPtr.Zero;
            UInt64 length = 0, maxLength = 2 * 1024 * 1024;

            var err = EDSDK.EDS_ERR_OK;

            // 当实时图像传输开启时，不断地循环
            while (isSessionOpened && (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
            {
                lock (sdkLock)
                {
                    err = EDSDK.EdsCreateMemoryStream(maxLength, out stream);       // 创建用于保存图像的流对象

                    if (err == EDSDK.EDS_ERR_OK)
                    {
                        err = EDSDK.EdsCreateEvfImageRef(stream, out evfImage);     // 创建evf图像对象

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsDownloadEvfImage(camera, evfImage);      // 从相机下载evf图像

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsGetPointer(stream, out evfStream);       // 获取流对象的流地址

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsGetLength(stream, out length);           // 获取流的长度
                    }
                }

                if (err == EDSDK.EDS_ERR_OK)
                    RenderBitmap(evfStream, length);    // 渲染图像

                if (stream != IntPtr.Zero)
                {
                    EDSDK.EdsRelease(stream);
                    stream = IntPtr.Zero;
                }

                if (evfImage != IntPtr.Zero)
                {
                    EDSDK.EdsRelease(evfImage);
                    evfImage = IntPtr.Zero;
                }

                if (evfStream != IntPtr.Zero)
                {
                    EDSDK.EdsRelease(evfStream);
                    evfStream = IntPtr.Zero;
                }
            }

            // 停止显示图像
            context.Send(n => { WriteableBitmap = null; }, null);
        }

        private void RenderBitmap(IntPtr evfStream, UInt64 length)
        {
            var data = new Byte[length];
            var bmpStartPoint = new System.Drawing.Point(0, 0);

            Marshal.Copy(evfStream, data, 0, (Int32)length);        // 从流地址拷贝一份到字节数组，再解码获取图像（如果可以写一个从指针解码图像，可以优化此步骤）

            using (var bmp = (Bitmap)imageConverter.ConvertFrom(data))      // 解码获取Bitmap
            {
                if (this.WriteableBitmap == null || this.width != bmp.Width || this.height != bmp.Height)
                {
                    // 第一次或宽高不对应时创建WriteableBitmap对象
                    this.width = bmp.Width;
                    this.height = bmp.Height;

                    // 通过线程同步上下文切换到主线程
                    context.Send(n =>
                    {
                        WriteableBitmap = new WriteableBitmap(this.width, this.height, 96, 96, PixelFormats.Bgr24, null);
                        backBuffer = WriteableBitmap.BackBuffer;        // 保存后台缓冲区指针
                        this.stride = WriteableBitmap.BackBufferStride; // 单行像素数据中的字节数
                        this.length = this.stride * this.height;        // 像素数据的总字节数
                    }, null);
                }

                // 获取Bitmap的像素数据指针
                var bmpData = bmp.LockBits(new Rectangle(bmpStartPoint, bmp.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                if (videoFileWriter != null)
                {
                    lock (videoFileWriter)
                    {
                        // 保存录像
                        if (!videoFileWriter.IsOpen)
                        {
                            var folder = VideoFolder ?? Environment.CurrentDirectory;

                            if (!Directory.Exists(folder))
                                Directory.CreateDirectory(folder);

                            var fileName = NamingRulesFunc?.Invoke() ?? (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds.ToString("0");
                            var filePath = Path.Combine(folder, fileName + ".mp4");

                            videoFileWriter.Open(filePath, this.width, this.height, 16, VideoCodec.MPEG4);      // 使用16FPS，MP4文件保存
                            spf = 1000 / 16;    // 计算一帧毫秒数
                            stopwatch.Restart();
                            frameIndex = 0;
                            videoFileWriter.WriteVideoFrame(bmpData);
                        }
                        else
                        {
                            // 写入视频帧时传入时间戳，否则录像时长将对不上
                            var frame_index = (UInt32)(stopwatch.ElapsedMilliseconds / spf);

                            if (frameIndex != frame_index)
                            {
                                frameIndex = frame_index;
                                videoFileWriter.WriteVideoFrame(bmpData, frameIndex);
                            }
                        }
                    }
                }

                // 将Bitmap的像素数据拷贝到WriteableBitmap
                if (this.stride == bmpData.Stride)
                    Memcpy(backBuffer, bmpData.Scan0, this.length);
                else
                {
                    var s = Math.Min(this.stride, bmpData.Stride);
                    var tPtr = backBuffer;
                    var sPtr = bmpData.Scan0;
                    for (var i = 0; i < this.height; i++)
                    {
                        Memcpy(tPtr, sPtr, s);
                        tPtr += this.stride;
                        sPtr += bmpData.Stride;
                    }
                }

                bmp.UnlockBits(bmpData);

                Interlocked.Exchange(ref newFrame, 1);
            }
        }

        private void OnRender(Object sender, EventArgs e)
        {
            var curRenderingTime = ((RenderingEventArgs)e).RenderingTime;

            if (curRenderingTime == lastRenderingTime)
                return;

            lastRenderingTime = curRenderingTime;

            if (Interlocked.CompareExchange(ref newFrame, 0, 1) != 1)
                return;

            var bmp = this.WriteableBitmap;
            bmp.Lock();
            bmp.AddDirtyRect(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            bmp.Unlock();
        }

        /// <summary>
        /// 复制指针数据到另一个指针
        /// </summary>
        /// <param name="dest">目标指针</param>
        /// <param name="source">源指针</param>
        /// <param name="length">字符长度</param>
        [DllImport("ntdll.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Memcpy(IntPtr dest, IntPtr source, Int32 length);

        /// <summary>
        /// 存储到计算机
        /// </summary>
        /// <returns></returns>
        private UInt32 SaveToHost()
        {
            var err = EDSDK.EDS_ERR_OK;

            if (SaveTo != (UInt32)EDSDK.EdsSaveTo.Host)
            {
                err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_SaveTo, 0, sizeof(UInt32), (UInt32)EDSDK.EdsSaveTo.Host);

                if (err == EDSDK.EDS_ERR_OK)
                {
                    // 通知相机主机计算机的可用磁盘容量
                    err = EDSDK.EdsSendStatusCommand(camera, EDSDK.CameraState_UILock, 0);

                    if (err == EDSDK.EDS_ERR_OK)
                    {
                        var capacity = new EDSDK.EdsCapacity
                        {
                            NumberOfFreeClusters = 0x7FFFFFFF,
                            BytesPerSector = 0x1000,
                            Reset = 1
                        };

                        err = EDSDK.EdsSetCapacity(camera, capacity);
                        EDSDK.EdsSendStatusCommand(camera, EDSDK.CameraState_UIUnLock, 0);
                    }
                }
            }

            return err;
        }

        private UInt32 SetImageQualityJpegOnly()
        {
            var list = new List<Int32>
            {
                (Int32)EDSDK.ImageQuality.EdsImageQuality_LJ,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_M1J,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_M2J,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_SJ,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_LJF,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_LJN,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_MJF,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_MJN,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_SJF,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_SJN,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_S1JF,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_S1JN,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_S2JF,
                (Int32)EDSDK.ImageQuality.EdsImageQuality_S3JF
            };

            if (list.Contains((Int32)ImageQuality))
                return EDSDK.EDS_ERR_OK;

            var i = ImageQualityDesc.FindIndex(q => list.Contains(q));

            if (i != -1)
                return EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_ImageQuality, 0, sizeof(UInt32), (UInt32)ImageQualityDesc[i]);
            else
                return EDSDK.EDS_ERR_INVALID_HANDLE;
        }

        #region 更新相机属性

        private void UpdatePropertyUInt32(UInt32 inPropertyID, UInt32 data)
        {
            switch (inPropertyID)
            {
                case EDSDK.PropID_AEMode:
                case EDSDK.PropID_AEModeSelect:
                    AEMode = data;
                    break;
                case EDSDK.PropID_Evf_OutputDevice:
                    EvfOutputDevice = data;
                    break;
                case EDSDK.PropID_SaveTo:
                    SaveTo = data;
                    break;
                case EDSDK.PropID_Evf_DepthOfFieldPreview:
                    DepthOfFieldPreview = data;
                    break;
                case EDSDK.PropID_ExposureCompensation:
                    ExposureCompensation = data;
                    break;
                case EDSDK.PropID_WhiteBalance:
                    WhiteBalance = data;
                    break;
                case EDSDK.PropID_MeteringMode:
                    MeteringMode = data;
                    break;
                case EDSDK.PropID_DriveMode:
                    DriveMode = data;
                    break;
                case EDSDK.PropID_Tv:
                    Tv = data;
                    break;
            }
        }

        private void UpdatePropertyDescUInt32(UInt32 inPropertyID, EDSDK.EdsPropertyDesc desc)
        {
            switch (inPropertyID)
            {
                case EDSDK.PropID_ISOSpeed:
                    ISOSpeedDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_ImageQuality:
                    ImageQualityDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_ExposureCompensation:
                    ExposureCompensationDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_WhiteBalance:
                    WhiteBalanceDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_MeteringMode:
                    MeteringModeDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_DriveMode:
                    DriveModeDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
                case EDSDK.PropID_Tv:
                    TvDesc = desc.PropDesc.Take(desc.NumElements).ToList();
                    break;
            }
        }

        #endregion

        private void Close(Boolean isDisposed = false)
        {
            // 关闭实时取景
            if ((EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
                Stop(out _);

            // 停止录像
            if (videoFileWriter != null)
                EndRecord(out _);

            // 结束会话
            if (isSessionOpened)
            {
                lock (sdkLock)
                {
                    if (EDSDK.EdsCloseSession(camera) == EDSDK.EDS_ERR_OK)
                        isSessionOpened = false;
                }
            }

            // 释放相机对象
            if (camera != IntPtr.Zero)
            {
                EDSDK.EdsRelease(camera);
                camera = IntPtr.Zero;
            }

            if (isDisposed)
            {
                GCHandle.FromIntPtr(handle).Free();     // 释放当前对象
                this.ImageSourceChanged = null;
                this.NewImage = null;
                this.NewVideo = null;
                this.NamingRulesFunc = null;
            }
            else
                EDSDK.EdsSetCameraAddedHandler(cameraAddedHandler, handle);     // 监听相机连接
        }

        private void SessionClosed()
        {
            var _camera = camera;
            camera = IntPtr.Zero;

            if (videoFileWriter != null)
            {
                lock (videoFileWriter)
                {
                    videoFileWriter.Close();
                    videoFileWriter = null;
                    stopwatch.Stop();
                    stopwatch = null;
                }
            }

            EDSDK.EdsRelease(_camera);
            EDSDK.EdsSetCameraAddedHandler(cameraAddedHandler, handle);
        }

        #endregion

        #region 回调事件

        private static UInt32 OnCameraAdded(IntPtr inContext)
        {
            var handle = GCHandle.FromIntPtr(inContext);
            var ins = (Camera)handle.Target;

            ins.context.Post(n => { ins.Play(out _); }, null);

            return EDSDK.EDS_ERR_OK;
        }

        private static UInt32 OnObjectEvent(UInt32 inEvent, IntPtr inRef, IntPtr inContext)
        {
            switch (inEvent)
            {
                case EDSDK.ObjectEvent_DirItemRequestTransfer:
                case EDSDK.ObjectEvent_DirItemCreated:
                    lock (sdkLock)
                    {
                        var handle = GCHandle.FromIntPtr(inContext);
                        var ins = (Camera)handle.Target;
                        var camera = ins.camera;

                        var err = EDSDK.EdsGetDirectoryItemInfo(inRef, out EDSDK.EdsDirectoryItemInfo dirItemInfo);
                        if (err == EDSDK.EDS_ERR_OK)
                        {
                            var isImg = dirItemInfo.szFileName.StartsWith("img", StringComparison.OrdinalIgnoreCase);
                            var folder = (isImg ? ins.ImageFolder : ins.VideoFolder) ?? Environment.CurrentDirectory;

                            if (!Directory.Exists(folder))
                                Directory.CreateDirectory(folder);

                            var names = dirItemInfo.szFileName.Split('.');
                            var fileName = ins.NamingRulesFunc?.Invoke() ?? names[0];
                            var fileExtension = names[1].ToLower();
                            var filePath = Path.Combine(folder, fileName + "." + fileExtension);

                            err = EDSDK.EdsCreateFileStream(filePath, EDSDK.EdsFileCreateDisposition.CreateAlways, EDSDK.EdsAccess.Write, out IntPtr stream);

                            if (err == EDSDK.EDS_ERR_OK)
                            {
                                err = EDSDK.EdsDownload(inRef, dirItemInfo.Size, stream);

                                if (err == EDSDK.EDS_ERR_OK)
                                {
                                    err = EDSDK.EdsDownloadComplete(inRef);

                                    if (err == EDSDK.EDS_ERR_OK)
                                    {
                                        ins.context.Post(n =>
                                        {
                                            if (isImg)
                                                ins.NewImage?.Invoke(filePath);
                                            else
                                                ins.NewVideo?.Invoke(filePath);
                                        }, null);
                                    }
                                }

                                EDSDK.EdsRelease(stream);
                            }

                            if (inEvent == EDSDK.ObjectEvent_DirItemCreated)
                                EDSDK.EdsDeleteDirectoryItem(inRef);
                        }
                    }
                    break;
                case EDSDK.ObjectEvent_DirItemContentChanged:
                    break;
            }

            EDSDK.EdsRelease(inRef);
            return EDSDK.EDS_ERR_OK;
        }

        private static UInt32 OnPropertyEvent(UInt32 inEvent, UInt32 inPropertyID, UInt32 inParam, IntPtr inContext)
        {
            var handle = GCHandle.FromIntPtr(inContext);
            var ins = (Camera)handle.Target;
            var camera = ins.camera;

            switch (inEvent)
            {
                case EDSDK.PropertyEvent_PropertyChanged:
                    {
                        lock (sdkLock)
                        {
                            var err = EDSDK.EdsGetPropertySize(camera, inPropertyID, 0, out EDSDK.EdsDataType dataType, out _);     // 获取属性类型
                            Console.WriteLine(inPropertyID);
                            if (err == EDSDK.EDS_ERR_OK)
                            {
                                switch (dataType)
                                {
                                    case EDSDK.EdsDataType.UInt32:
                                        {
                                            err = EDSDK.EdsGetPropertyData(camera, inPropertyID, 0, out UInt32 data);       // 获取属性值

                                            if (err == EDSDK.EDS_ERR_OK)
                                                ins.UpdatePropertyUInt32(inPropertyID, data);
                                        }
                                        break;
                                }
                            }
                        }
                    }
                    break;
                case EDSDK.PropertyEvent_PropertyDescChanged:
                    {
                        lock (sdkLock)
                        {
                            var err = EDSDK.EdsGetPropertySize(camera, inPropertyID, 0, out EDSDK.EdsDataType dataType, out _);     // 获取属性类型

                            if (err == EDSDK.EDS_ERR_OK)
                            {
                                switch (dataType)
                                {
                                    case EDSDK.EdsDataType.UInt32:
                                        {
                                            err = EDSDK.EdsGetPropertyDesc(camera, inPropertyID, out EDSDK.EdsPropertyDesc desc);       // 获取属性的可能值

                                            if (err == EDSDK.EDS_ERR_OK)
                                                ins.UpdatePropertyDescUInt32(inPropertyID, desc);
                                        }
                                        break;
                                }
                            }
                        }
                    }
                    break;
            }

            return EDSDK.EDS_ERR_OK;
        }

        private static UInt32 OnStateEvent(UInt32 inEvent, UInt32 inParameter, IntPtr inContext)
        {
            switch (inEvent)
            {
                case EDSDK.StateEvent_Shutdown:
                    var handle = GCHandle.FromIntPtr(inContext);
                    var ins = (Camera)handle.Target;
                    ins.isSessionOpened = false;
                    ins.context.Post(n => { ((Camera)n).SessionClosed(); }, ins);
                    break;
            }

            return EDSDK.EDS_ERR_OK;
        }

        #endregion

        #region 属性

        /// <summary>
        /// 相机名称
        /// </summary>
        public String CameraName => cameraName;

        /// <summary>
        /// 图像源改变事件回调通知
        /// </summary>
        public Action<ImageSource> ImageSourceChanged { get; set; }

        /// <summary>
        /// 新照片回调通知
        /// </summary>
        public Action<String> NewImage { get; set; }

        /// <summary>
        /// 新录像回调通知
        /// </summary>
        public Action<String> NewVideo { get; set; }

        /// <summary>
        /// 储存图像文件夹
        /// </summary>
        public String ImageFolder { get; set; }

        /// <summary>
        /// 储存录像文件夹
        /// </summary>
        public String VideoFolder { get; set; }

        /// <summary>
        /// 命名规则
        /// </summary>
        public Func<String> NamingRulesFunc { get; set; }

        #endregion

        #region 字段

        // 回调函数处理
        private static EDSDK.EdsCameraAddedHandler cameraAddedHandler;
        private static EDSDK.EdsObjectEventHandler objectEventHandler;
        private static EDSDK.EdsPropertyEventHandler propertyEventHandler;
        private static EDSDK.EdsStateEventHandler stateEventHandler;
        private static Boolean isSDKLoaded;     // 指示EDSDK是否初始化
        private static Object sdkLock = new Object();   // EDSDK的API不能同时调用，需要一个锁

        private IntPtr camera;      // 相机对象
        private IntPtr handle;      // 当前类实例的GC句柄
        private String cameraName;  // 相机名称
        private Thread thread_evf;  // 循环采集EvfImage的STA线程
        private Int32 deviceSize;   // EDSDK.PropID_Evf_OutputDevice的大小
        private Boolean isSessionOpened;    // 是否打开会话
        private SynchronizationContext context = SynchronizationContext.Current;    // 表示类创建时的线程同步上下文，通常是主线程
        private ImageConverter imageConverter = new ImageConverter();       // 图像转换器，转换后的对象是Bitmap类型

        private WriteableBitmap writeableBitmap;
        /// <summary>
        /// WPF的一个高性能渲染图像，利用后台缓冲区，渲染图像时不必每次都切换线程
        /// </summary>
        private WriteableBitmap WriteableBitmap
        {
            get => this.writeableBitmap;
            set
            {
                if (this.writeableBitmap == value)
                    return;

                if (this.writeableBitmap == null)
                    CompositionTarget.Rendering += OnRender;
                else if (value == null)
                    CompositionTarget.Rendering -= OnRender;

                this.writeableBitmap = value;
                this.ImageSourceChanged?.Invoke(value);
            }
        }

        private Int32 width, height, stride, length;        // WriteableBitmap的宽、高、一行数据的字节长度、所有行字节长度，用于计算如何将Bitmap写入WriteableBitmap的后台缓冲区
        private IntPtr backBuffer;      // WriteableBitmap的后台缓冲区
        private Int32 newFrame;         // 一个原子锁，1表示WriteableBitmap的后台缓冲区有新的数据
        private TimeSpan lastRenderingTime;     // CompositionTarget.Rendering事件的上一次渲染时间，避免同一时刻渲染多次

        private VideoFileWriter videoFileWriter;        // 录像文件写入对象
        private Stopwatch stopwatch;    // 录像计时
        private Int64 spf;              // 一帧多少毫秒
        private UInt32 frameIndex;      // 当前帧

        #region 相机属性

        private UInt32 AEMode;                  // AE模式转盘
        private UInt32 EvfOutputDevice;
        private UInt32 SaveTo;
        private UInt32 DepthOfFieldPreview;
        private UInt32 ISOSpeed;                // ISO感光度
        private UInt32 ImageQuality;            // 图像质量
        private UInt32 ExposureCompensation;    // 曝光补偿
        private UInt32 WhiteBalance;            // 白平衡类型
        private UInt32 MeteringMode;            // 测光模式
        private UInt32 DriveMode;               // 驱动模式下相机的设置值
        private UInt32 Tv;                      // 快门速度

        #endregion

        #region 相机属性描述

        private List<Int32> ISOSpeedDesc;
        private List<Int32> ImageQualityDesc;
        private List<Int32> ExposureCompensationDesc;
        private List<Int32> WhiteBalanceDesc;
        private List<Int32> MeteringModeDesc;
        private List<Int32> DriveModeDesc;
        private List<Int32> TvDesc;

        #endregion

        #endregion
    }
}
