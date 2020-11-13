using Accord.Video.FFMPEG;
using EDSDKLib;
using System;
using System.Drawing;
using System.IO;
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
            handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        }

        #region 公开方法

        public Boolean Init(out String errMsg)
        {
            errMsg = null;

            lock (sdkLock)
            {
                var err = InitCamera();
                var ret = err == EDSDK.EDS_ERR_OK;

                if (!ret)
                {
                    errMsg = "未检测到相机，错误代码：" + err;
                    Close();
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

            UInt32 err = EDSDK.EDS_ERR_OK;

            lock (sdkLock)
            {
                if (AEMode != EDSDK.AEMode_Tv)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_Mode, 0, sizeof(UInt32), EDSDK.AEMode_Tv);

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

            if (camera == IntPtr.Zero)
                return true;

            var err = EDSDK.EDS_ERR_OK;

            lock (sdkLock)
            {
                if (DepthOfFieldPreview != EDSDK.EvfDepthOfFieldPreview_OFF)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_DepthOfFieldPreview, 0, sizeof(UInt32), EDSDK.EvfDepthOfFieldPreview_OFF);

                if (err == EDSDK.EDS_ERR_OK && (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, deviceSize, EvfOutputDevice & ~EDSDK.EvfOutputDevice_PC);
            }

            if(err != EDSDK.EDS_ERR_OK)
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
                    err = EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_TakePicture, 0);

                    if (err == EDSDK.EDS_ERR_OK)
                        err = EDSDK.EdsSendCommand(camera, EDSDK.CameraCommand_PressShutterButton, (Int32)EDSDK.EdsShutterButton.CameraCommand_ShutterButton_OFF);
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
                err = EDSDK.EdsInitializeSDK();

                if (err != EDSDK.EDS_ERR_OK)
                    return err;

                isSDKLoaded = true;
            }

            err = GetFirstCamera(out camera);

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

                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_ProductName, 0, out cameraName);

                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsGetPropertySize(camera, EDSDK.PropID_Evf_OutputDevice, 0, out _, out deviceSize);

                // 保存到计算机
                if (err == EDSDK.EDS_ERR_OK)
                    err = SaveToHost();

                // 设置曝光
                if (err == EDSDK.EDS_ERR_OK)
                    err = EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_ISOSpeed, 0, sizeof(UInt32), 128);
            }

            return err;
        }

        private void ReadEvf()
        {
            SpinWait.SpinUntil(() => (EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0, 5000);

            IntPtr stream = IntPtr.Zero;
            IntPtr evfImage = IntPtr.Zero;
            IntPtr evfStream = IntPtr.Zero;
            UInt64 length = 0, maxLength = 2 * 1024 * 1024;
            var data = new Byte[maxLength];
            var bmpStartPoint = new System.Drawing.Point(0, 0);
            var startRecordTime = 0;

            var err = EDSDK.EDS_ERR_OK;

            while ((EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
            {
                lock (sdkLock)
                {
                    err = EDSDK.EdsCreateMemoryStream(maxLength, out stream);

                    if (err == EDSDK.EDS_ERR_OK)
                    {
                        err = EDSDK.EdsCreateEvfImageRef(stream, out evfImage);

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsDownloadEvfImage(camera, evfImage);

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsGetPointer(stream, out evfStream);

                        if (err == EDSDK.EDS_ERR_OK)
                            err = EDSDK.EdsGetLength(stream, out length);
                    }
                }

                if (err == EDSDK.EDS_ERR_OK)
                {
                    Marshal.Copy(evfStream, data, 0, (Int32)length);

                    using (var bmp = (Bitmap)imageConverter.ConvertFrom(data))
                    {
                        if (this.Bitmap == null || this.width != bmp.Width || this.height != bmp.Height)
                        {
                            this.width = bmp.Width;
                            this.height = bmp.Height;

                            context.Send(n =>
                            {
                                Bitmap = new WriteableBitmap(this.width, this.height, 96, 96, PixelFormats.Bgr24, null);
                                backBuffer = Bitmap.BackBuffer;
                                this.stride = Bitmap.BackBufferStride;
                                this.length = this.stride * this.height;
                            }, null);
                        }

                        var bmpData = bmp.LockBits(new Rectangle(bmpStartPoint, bmp.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

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

                                    videoFileWriter.Open(filePath, this.width, this.height);
                                    startRecordTime = Environment.TickCount;
                                }

                                videoFileWriter.WriteVideoFrame(bmp, TimeSpan.FromMilliseconds(Environment.TickCount - startRecordTime));
                            }
                        }
                    }
                }

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

            context.Send(n => { Bitmap = null; }, null);
        }

        private void OnRender(Object sender, EventArgs e)
        {
            var curRenderingTime = ((RenderingEventArgs)e).RenderingTime;

            if (curRenderingTime == lastRenderingTime)
                return;

            lastRenderingTime = curRenderingTime;

            if (Interlocked.CompareExchange(ref newFrame, 0, 1) != 1)
                return;

            var bmp = this.Bitmap;
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
            }
        }

        #endregion

        private void Close(Boolean isDisposed = false)
        {
            if ((EvfOutputDevice & EDSDK.EvfOutputDevice_PC) != 0)
                Stop(out _);

            if (videoFileWriter != null)
                EndRecord(out _);

            if (isSessionOpened)
            {
                lock (sdkLock)
                {
                    if (EDSDK.EdsCloseSession(camera) == EDSDK.EDS_ERR_OK)
                        isSessionOpened = false;
                }
            }

            if (camera != IntPtr.Zero)
            {
                EDSDK.EdsRelease(camera);
                camera = IntPtr.Zero;
            }

            if (isDisposed)
            {
                GCHandle.FromIntPtr(handle).Free();
                ImageSourceChanged = null;
            }
            else
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
            switch (inEvent)
            {
                case EDSDK.PropertyEvent_PropertyChanged:
                    {
                        var handle = GCHandle.FromIntPtr(inContext);
                        var ins = (Camera)handle.Target;
                        var camera = ins.camera;

                        lock (sdkLock)
                        {
                            var err = EDSDK.EdsGetPropertySize(camera, inPropertyID, 0, out EDSDK.EdsDataType dataType, out _);
                            Console.WriteLine(inPropertyID);
                            if (err == EDSDK.EDS_ERR_OK)
                            {
                                switch (dataType)
                                {
                                    case EDSDK.EdsDataType.UInt32:
                                        {
                                            err = EDSDK.EdsGetPropertyData(camera, inPropertyID, 0, out UInt32 data);

                                            if (err == EDSDK.EDS_ERR_OK)
                                                ins.UpdatePropertyUInt32(inPropertyID, data);
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
                    ins.context.Post(n => { ((Camera)n).Close(); }, ins);
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

        private static EDSDK.EdsCameraAddedHandler cameraAddedHandler;
        private static EDSDK.EdsObjectEventHandler objectEventHandler;
        private static EDSDK.EdsPropertyEventHandler propertyEventHandler;
        private static EDSDK.EdsStateEventHandler stateEventHandler;
        private static Boolean isSDKLoaded;
        private static Object sdkLock = new Object();

        private IntPtr camera;
        private IntPtr handle;
        private String cameraName;
        private Thread thread_evf;
        private Int32 deviceSize;
        private Boolean isSessionOpened;
        private SynchronizationContext context = SynchronizationContext.Current;
        private ImageConverter imageConverter = new ImageConverter();

        private WriteableBitmap bitmap;
        private WriteableBitmap Bitmap
        {
            get => this.bitmap;
            set
            {
                if (this.bitmap == value)
                    return;

                if (this.bitmap == null)
                    CompositionTarget.Rendering += OnRender;
                else if (value == null)
                    CompositionTarget.Rendering -= OnRender;

                this.bitmap = value;
                this.ImageSourceChanged?.Invoke(value);
            }
        }

        private Int32 width, height, stride, length;
        private IntPtr backBuffer;
        private Int32 newFrame;
        private TimeSpan lastRenderingTime;

        private VideoFileWriter videoFileWriter;

        #region 相机属性

        private UInt32 AEMode;
        private UInt32 EvfOutputDevice;
        private UInt32 SaveTo;
        private UInt32 DepthOfFieldPreview;

        #endregion

        #endregion
    }
}
