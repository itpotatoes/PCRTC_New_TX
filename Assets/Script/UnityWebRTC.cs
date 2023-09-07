using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using LZ4;
using WebSocketSharp;
using UnityEngine;
using Unity.WebRTC;

//AzureKinect SDK
using Microsoft.Azure.Kinect.Sensor;
using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;

public class UnityWebRTC : MonoBehaviour
{
    
    
    //kinect 변수
    Device kinect;
    //PointCloud의 점
    int num;


    private RTCPeerConnection peerConnection;
    private RTCDataChannel dataChannel;
    private ConcurrentQueue<(Vector3[] vertices, Color32[] colors)> meshDataQueue = new ConcurrentQueue<(Vector3[] vertices, Color32[] colors)>();
    private ConcurrentQueue<(Vector3[] vertices, Color32[] colors)> meshDataQueue2 = new ConcurrentQueue<(Vector3[] vertices, Color32[] colors)>();
    
    [SerializeField]
    private string signalingUrl = "ws://lyj.leafserver.kr:3000"; // 시그널링 서버 주소

    private WebSocket webSocket;

    

    Mesh mesh;
    //PointCloud의 각 점의 좌표의 배열
    Vector3[] vertices;
    //PointCloud의 각 점에 대응하는 색의 배열
    Color32[] colors;
    // PointCloud 배열 변호를 기록
    int[] indices;

    int[] sendindices;
    //좌표 변환
    Transformation transformation;


        void Start()
    {
        
        //Kinect 초기화
        InitKinect();
        
        //PointCloud 준비
        InitMesh();
        
        //Kinect 데이터 가져오기
        Task t = KinectLoop();
       StartCoroutine(UpdateMesh());

        
        //WebRTC 초기화
        WebRTC.Initialize();
        // ICE 서버 구성
        RTCConfiguration config = new RTCConfiguration();
        config.iceServers = new[]
        {
            new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
            new RTCIceServer { urls = new[] { "stun:stun1.l.google.com:19302" } },
            new RTCIceServer { urls = new[] { "stun:stun2.l.google.com:19302" } },
            new RTCIceServer { urls = new[] { "stun:stun3.l.google.com:19302" } },
            new RTCIceServer { urls = new[] { "stun:stun4.l.google.com:19302" } }
        };
        
        
        
        peerConnection = new RTCPeerConnection(ref config);
        RTCDataChannelInit dataconfig = new RTCDataChannelInit();
        dataChannel = peerConnection.CreateDataChannel("data",dataconfig);
        Debug.Log("Data channel created.");
        dataChannel.OnOpen += () =>
        {
            Debug.Log("Data channel opened (dataChannel)");
            dataChannel.Send("OPEN (dataChannel)");
        };
        dataChannel.OnClose += () =>
        {
            Debug.Log("Data channel closed (dataChannel)");
        };
        dataChannel.OnMessage += (byte[] data) =>
        {
            string message = System.Text.Encoding.UTF8.GetString(data);
            Debug.Log($"Received message (dataChannel) : {message}");
        };
        
        // Set up the OnDataChannel event handler
        peerConnection.OnDataChannel = channel =>
        {
            dataChannel = channel;
        };

        // Add ICE candidate event handler
        peerConnection.OnIceCandidate += OnIceCandidate;
        peerConnection.OnIceConnectionChange = state =>
        {
            Debug.Log(state);
        };

        
        //시그널링 서버와 연결
        webSocket = new WebSocket(signalingUrl);
        webSocket.OnOpen += (sender, e) =>
        {
            Debug.Log("WebSocket connection opened.");
            webSocket.Send("TX");
        };
        webSocket.OnMessage += OnWebSocketMessage;
        webSocket.OnError += (sender, e) => Debug.LogError("WebSocket error: " + e.Exception);
        webSocket.OnClose += (sender, e) => Debug.Log("WebSocket connection closed.");

        webSocket.ConnectAsync();


        

    }


        //Kinect 초기화
    private void InitKinect()
    {
        // 0번째 Kinect와 연결
        kinect = Device.Open(0);
        
        //Kinect 모드 설정
        kinect.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            SynchronizedImagesOnly = true,
            CameraFPS = FPS.FPS30
        });
        
        //좌표 변환 호출
        transformation = kinect.GetCalibration().CreateTransformation();

    }
    
    //PointCloud 준비
    private void InitMesh()
    {
        // 가로 세로 취득
        int width = kinect.GetCalibration().DepthCameraCalibration.ResolutionWidth;
        int height = kinect.GetCalibration().DepthCameraCalibration.ResolutionHeight;
        num = width * height;
        Debug.LogError("num" + num);
        mesh = new Mesh();
        //65535 점 이상을 표현하기 위해 설정
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        
        // 저장공간 확보
        vertices = new Vector3[num];
        colors = new Color32[num];
        indices = new int[num];
        
        sendindices = new int[num];
        //PointCloud 배열 번호를 기록

        for (int i = 0; i < num; i++)
        {
            indices[i] = i;
        }
        
        for (int i = 0; i < num; i++)
        {
            sendindices[i] = i;
        }

        //mesh에 값 전달
        mesh.vertices = vertices;
        mesh.colors32 = colors;
        mesh.SetIndices(indices,MeshTopology.Points,0);
        
        //메쉬를 MeshFilter에 적용
        gameObject.GetComponent<MeshFilter>().mesh = mesh;
    }
    
    private long lastFrameTimestamp = -1;
    private int frameCount = 0;
    private Stopwatch stopwatch = new Stopwatch();
    
    //Kinect 데이터 가져오기
    private async Task KinectLoop()
    {
        stopwatch.Start();
        while (true)
        {
            using (Capture capture = await Task.Run(() => kinect.GetCapture()).ConfigureAwait(true))
            {
                
                Image colorImage = transformation.ColorImageToDepthCamera(capture);
                BGRA[] colorArray = colorImage.GetPixels<BGRA>().ToArray();
                Image xyzImage = transformation.DepthImageToPointCloud(capture.Depth);
                Short3[] xyzArray = xyzImage.GetPixels<Short3>().ToArray();

                // Depth의 타임스탬프 확인
                long currentTimestamp = capture.Depth.DeviceTimestamp.Ticks;
                if(currentTimestamp == lastFrameTimestamp)
                {
                    continue; // 현재 프레임은 이전 프레임과 동일하므로 처리를 건너뛰고 다음 프레임을 기다립니다.
                }
                lastFrameTimestamp = currentTimestamp;

                frameCount++;

                if (stopwatch.ElapsedMilliseconds >= 1000) // 1초마다 FPS 계산
                {
                    float fps = frameCount / (stopwatch.ElapsedMilliseconds / 1000.0f);
                    Debug.Log($"Current FPS: {fps}");

                    frameCount = 0;
                    stopwatch.Reset();
                    stopwatch.Start();
                }
                
                

                for (int i = 0; i < num; i++)
                {
                    vertices[i].x = xyzArray[i].X * 0.1f;
                    vertices[i].y = -xyzArray[i].Y * 0.1f;
                    vertices[i].z = xyzArray[i].Z * 0.1f;

                    colors[i].b = colorArray[i].B;
                    colors[i].g = colorArray[i].G;
                    colors[i].r = colorArray[i].R;
                    colors[i].a = 255;
                }
            }
            
            List<int> zeroIndices = new List<int>();

            long unixTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
            List<byte> data = new List<byte>();
            
            
            for (int i = 0; i < num; i++)
            {
                if (vertices[i].x == 0 && vertices[i].y == 0 && vertices[i].z == 0 &&
                    colors[i].r == 0 && colors[i].g == 0 && colors[i].b == 0)
                {
                    zeroIndices.Add(i);
                }
                else
                {
                    data.AddRange(BitConverter.GetBytes(vertices[i].x));
                    data.AddRange(BitConverter.GetBytes(vertices[i].y));
                    data.AddRange(BitConverter.GetBytes(vertices[i].z));
                    data.Add(colors[i].r);
                    data.Add(colors[i].g);
                    data.Add(colors[i].b);
                    data.Add(colors[i].a);
                }
            }

            int zeroIndicesCount = zeroIndices.Count;
            Debug.Log(zeroIndices);
            data.AddRange(BitConverter.GetBytes(zeroIndicesCount));

            foreach (int index in zeroIndices)
            {
                data.AddRange(BitConverter.GetBytes(index));
            }

            data.AddRange(BitConverter.GetBytes(unixTimestamp));
            
            
            
            /*  string directoryPath = @"D:\Loss\Comp_New";
              string filename = $"TX_{unixTimestamp}.dat";
              string fullPath = Path.Combine(directoryPath, filename);
  
            */
            
          //byte[] compressedData = CompressData(data);
          byte[] compressedData2 = CompressData2(data);
           
           //File.WriteAllBytes(fullPath, compressedData.ToArray());
           
           
           Debug.LogError($"Data size: {data.Count} bytes");
            //Debug.LogError($"compressedData: {compressedData.Length} bytes");
            Debug.LogError($"compressedData2: {compressedData2.Length} bytes");
            
            //SendData(data.ToArray());
           SendData(compressedData2);
            int endSignal = 0;
            byte[] endSignalBytes = BitConverter.GetBytes(endSignal);
                      
        
            SendData(endSignalBytes);
           // ReceiveChunk(endSignalBytes);
           mesh.vertices = vertices;
           mesh.colors32 = colors;
            mesh.RecalculateBounds();
            

        }
    }
    
    
    private List<byte> receivedData2 = new List<byte>();
    private List<byte> receivedDataBuffer = new List<byte>();

    private void OnDestroy()
    {
        kinect.StopCameras();
        peerConnection.Close();
        webSocket.Close();
        WebRTC.Dispose();
    }
    // 데이터 압축 메서드
    public byte[] CompressData(List<byte> data)
    {
        using (MemoryStream output = new MemoryStream())
        {
            using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data.ToArray(), 0, data.Count);
            }
            return output.ToArray();
        }
    }
    
    public byte[] CompressData2(List<byte> data)
    {
        using (MemoryStream output = new MemoryStream())
        {
            using (LZ4Stream lz4Stream = new LZ4Stream(output, LZ4StreamMode.Compress))
            {
                lz4Stream.Write(data.ToArray(), 0, data.Count);
            }
            return output.ToArray();
        }
    }
    
    private IEnumerator CreateOfferCoroutine()
    {
        var op = peerConnection.CreateOffer();
        yield return op;

        if (op.IsError)
        {
            Debug.LogError("Failed to create SDP offer: " + op.Error);
            yield break;
        }

        var offer = op.Desc;
        peerConnection.SetLocalDescription(ref offer);
        var jsonOffer = JsonUtility.ToJson(offer);
        if (webSocket.ReadyState == WebSocketState.Open)
        {
            webSocket.Send(jsonOffer);
        }
        else
        {
            Debug.LogError("WebSocket state: " + webSocket.ReadyState);
            Debug.LogError("WebSocket is not open. Cannot send offer.");
        }
    }
    

     private void OnWebSocketMessage(object sender, MessageEventArgs e)
    {
        string dataAsString = Encoding.UTF8.GetString(e.RawData);
        Debug.Log("WebRTC_RX : Received message from signaling server as string: " + dataAsString);
        // Deserialize the received message
        var message = JsonUtility.FromJson<SignalingMessage>(dataAsString);
        Debug.Log("수신받은 데이터"+"type: " + message.type + "candidate: " + message.candidate + "sdp: " + message.sdp);
    
        if (!string.IsNullOrEmpty(message.sdp))
        {
            // Convert message.type to RTCSdpType
            if (System.Enum.TryParse<RTCSdpType>(message.type, true, out var sdpType))
            {
                // The message is an SDP
                var sessionDesc = new RTCSessionDescription { type = sdpType, sdp = message.sdp };
                if (sdpType == RTCSdpType.Offer)
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        Debug.Log($"OnWebSocketMessage: {sdpType}");
                        StartCoroutine(SetRemoteDescriptionAndCreateAnswerCoroutine(sessionDesc));
                    });
                }
                else if (sdpType == RTCSdpType.Answer)
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        Debug.Log($"OnWebSocketMessage: {sdpType}");
                        StartCoroutine(SetRemoteDescriptionCoroutine(sessionDesc));
                    });
                }
            }
            else
            {
                Debug.LogError($"Failed to parse SDP type: {message.type}");
            }
        }
        else if (!string.IsNullOrEmpty(message.candidate))
        {
            RTCIceCandidateInit iceCandidateInit = new RTCIceCandidateInit
            {
                candidate = message.candidate,
                sdpMid = message.sdpMid,
                sdpMLineIndex = message.sdpMLineIndex.GetValueOrDefault()
            };
            RTCIceCandidate iceCandidate = new RTCIceCandidate(iceCandidateInit);
            peerConnection.AddIceCandidate(iceCandidate);
        }   
        else if (message.type == "start")
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                // 로컬 피어가 Offer를 생성하도록 설정
                StartCoroutine(CreateOfferCoroutine());
            });
        }
    }
     
     private IEnumerator SetRemoteDescriptionCoroutine(RTCSessionDescription sessionDesc)
     {
         var opSetRemoteDesc = peerConnection.SetRemoteDescription(ref sessionDesc);
         yield return opSetRemoteDesc;

         if (opSetRemoteDesc.IsError)
         {
             Debug.LogError("Failed to set remote description: " + opSetRemoteDesc.Error.message);

             yield break;
         }
     }
    
    private void OnIceCandidate(RTCIceCandidate iceCandidate)
    {
        Debug.Log("Local ICE candidate: " + iceCandidate.Candidate);
        SignalingMessage message = new SignalingMessage
        {
            candidate = iceCandidate.Candidate,
            sdpMid = iceCandidate.SdpMid,
            sdpMLineIndex = iceCandidate.SdpMLineIndex
        };
        SendSignalingMessage(message);
    }
    private float coolDown = 0.5f;
    private float updateTime = 0.0f;
    
    private void Update()
    {
    }
    
    private IEnumerator UpdateMesh()
    {
        while (true)
        {
            
            if (meshDataQueue2.TryDequeue(out (Vector3[] vertices, Color32[] colors) meshData))
            {
                mesh.vertices = meshData.vertices;
                mesh.colors32 = meshData.colors;
                mesh.RecalculateBounds();
                
                
            }

            yield return null;
        }
    }




    private void SendData(byte[] data)
    {
        int chunkSize =256*1024;
        int dataSize = data.Length;
        int offset = 0;
        int totalSize = dataSize;

        byte[] buffer = new byte[totalSize];

        BitConverter.GetBytes(dataSize).CopyTo(buffer, 0);

        int sentChunkCount = 0;

        if (dataChannel != null && dataChannel.ReadyState == RTCDataChannelState.Open)
        {

            // If data size is 0, send it directly and return
            if (data.Length == 0)
            {
                dataChannel.Send(data);
                Debug.Log("Sent end-of-data signal.");
                return;
            }
            
            while (offset < totalSize)
            {
                int bytesToSend = Math.Min(chunkSize, totalSize - offset);
                byte[] chunk = new byte[bytesToSend];
                Array.Copy(data, offset, chunk, 0, bytesToSend);
                dataChannel.Send(chunk);
                offset += bytesToSend;
                sentChunkCount++;
                Debug.Log($"Sent chunk {sentChunkCount} of size {bytesToSend} bytes.");

            }
            Debug.Log($"Total chunk size: {totalSize} bytes.");
        }
        else
        {
         
            
            //Debug.Log("Data channel is not open. Cannot send data.");
        }
    }


    [System.Serializable]
    private class SignalingMessage
    {
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int? sdpMLineIndex;
    }
    private void SendSignalingMessage(object message)
    {
        if (webSocket != null && webSocket.IsAlive)
        {
            string jsonMessage = JsonUtility.ToJson(message);
            webSocket.Send(jsonMessage);
        }

    }

    private IEnumerator SetRemoteDescriptionAndCreateAnswerCoroutine(RTCSessionDescription sessionDesc)
    {
        var opSetRemoteDesc = peerConnection.SetRemoteDescription(ref sessionDesc);
        yield return opSetRemoteDesc;

        if (opSetRemoteDesc.IsError)
        {
            Debug.LogError("Failed to set remote description: " + opSetRemoteDesc.Error.message);
            yield break;
        }

        if (sessionDesc.type == RTCSdpType.Offer)
        {
            StartCoroutine(SendDataChannelMessageWhenReady());
        }
    }
    
    private IEnumerator SendDataChannelMessageWhenReady()
    {
        // Wait until the data channel is ready.
        while (dataChannel.ReadyState != RTCDataChannelState.Open)
        {
            yield return null;
        }

        // Send a message through the data channel.
        string messageToSend = "Hello, World!";
        byte[] messageData = Encoding.UTF8.GetBytes(messageToSend);
        dataChannel.Send(messageData);
        dataChannel.Send(messageToSend);
        Debug.Log("Data channel state: " + dataChannel.ReadyState);
        Debug.Log("Data channel protocol: " + dataChannel.Protocol);
        Debug.Log("Data channel buffered amount: " + dataChannel.BufferedAmount);
    }

}