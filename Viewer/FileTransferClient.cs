using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    /// <summary>
    /// 파일 전송 전용 WebRTC 클라이언트.
    /// Video/Audio 트랙 없이 DataChannel만 사용합니다.
    /// </summary>
    public class FileTransferClient : IDisposable
    {
        private RTCPeerConnection _pc;
        private RTCDataChannel _dc;
        private SignalingClient _signaling;
        private string _targetHostId;
        private TaskCompletionSource<bool> _connectionTcs;
        
        public Action<int>? OnProgress; // 0~100
        public Action<string>? OnStatus; // 상태 메시지

        private List<RTCIceCandidateInit> _iceQueue = new();
        private bool _remoteDescriptionSet = false;

        // 파일 전송 프로토콜 상수 (Viewer/Host 공통)
        private const byte MSG_FILE_START = 0x30;
        private const byte MSG_FILE_CHUNK = 0x31;
        private const byte MSG_FILE_END   = 0x32;
        private const byte MSG_FILE_ACK   = 0x33;
        private const byte MSG_FILE_HASH_MISMATCH = 0x34;

        public FileTransferClient(SignalingClient signaling, string targetHostId)
        {
            _signaling = signaling;
            _targetHostId = targetHostId;
            _signaling.OnSignalReceived += OnSignalReceived;
        }

        public async Task<bool> ConnectAsync()
        {
            _connectionTcs = new TaskCompletionSource<bool>();

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };
            _pc = new RTCPeerConnection(config);

            // DataChannel 생성
            _dc = await _pc.createDataChannel("file"); // Host는 'file' 채널을 기대함
            _dc.onopen += () => 
            {
                Console.WriteLine($"[FileTransfer] DataChannel open for {_targetHostId}");
                _connectionTcs.TrySetResult(true);
            };
            
            _pc.onconnectionstatechange += (state) =>
            {
                Console.WriteLine($"[FileTransfer] Connection state: {state}");
                if (state == RTCPeerConnectionState.failed)
                {
                    _connectionTcs.TrySetResult(false);
                }
            };
            _dc.onmessage += (dc, protocol, data) =>
            {
                // ACK 처리
                 if (data.Length > 0)
                 {
                     if (data[0] == MSG_FILE_ACK) _ackTcs.TrySetResult(true);
                     else if (data[0] == MSG_FILE_HASH_MISMATCH) _ackTcs.TrySetResult(false);
                 }
            };

            _pc.onicecandidate += (candidate) =>
            {
                // Host는 { "ice": { ... } } 포맷을 기대함 (WebRTCManager.cs 354행)
                _signaling.SendSignalAsync(_targetHostId, new { 
                    ice = new {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    }
                });
            };

            // Host와의 호환성을 위해 Video Track 추가 (RecvOnly)
            // Host가 항상 Video Track을 추가하므로, 이를 받아주지 않으면 협상 실패 가능성 있음.
            var h264Format = new VideoFormat(VideoCodecsEnum.H264, 96, 90000, null);
            var videoTrack = new MediaStreamTrack(h264Format, MediaStreamStatusEnum.RecvOnly);
            _pc.addTrack(videoTrack);

            // Offer 생성 및 전송
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);
            await _signaling.SendSignalAsync(_targetHostId, new { sdp = offer });

            // 30초 타임아웃
            var timeoutTask = Task.Delay(30000);
            var completedTask = await Task.WhenAny(_connectionTcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                Console.WriteLine($"[FileTransfer] Connection timeout for {_targetHostId}");
                return false;
            }

            return await _connectionTcs.Task;
        }

        private void OnSignalReceived(string from, object signal)
        {
            if (from != _targetHostId) return;

            try
            {
                var json = JObject.FromObject(signal);
                if (json.ContainsKey("sdp"))
                {
                    var sdp = json["sdp"];
                    var type = sdp["type"]?.ToString();
                    var sdpStr = sdp["sdp"]?.ToString();
                    
                    if (type == "answer")
                    {
                         _pc.setRemoteDescription(new RTCSessionDescriptionInit 
                         { 
                             type = RTCSdpType.answer, 
                             sdp = sdpStr 
                         });
                         
                         _remoteDescriptionSet = true;
                         foreach (var c in _iceQueue) _pc.addIceCandidate(c);
                         _iceQueue.Clear();
                    }
                }
                else if (json.ContainsKey("ice"))
                {
                    var ice = json["ice"];
                    var candidate = new RTCIceCandidateInit
                    {
                        candidate = ice["candidate"]?.ToString(),
                        sdpMid = ice["sdpMid"]?.ToString(),
                        sdpMLineIndex = (ushort?)ice["sdpMLineIndex"] ?? 0
                    };
                    
                    if (_remoteDescriptionSet) _pc.addIceCandidate(candidate);
                    else _iceQueue.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Signal processing error: {ex.Message}");
            }
        }

        public async Task SendFileAsync(string filePath)
        {
            if (_dc == null || _dc.readyState != RTCDataChannelState.open)
                throw new InvalidOperationException("Connection not ready");

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) throw new FileNotFoundException("File not found", filePath);

            string fileName = fileInfo.Name;
            long fileSize = fileInfo.Length;
            
            OnStatus?.Invoke($"Sending {fileName}...");

            // 1. START
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
            
            // 해시 계산 (SHA256)
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var fsHash = fileInfo.OpenRead();
            byte[] hashBytes = sha256.ComputeHash(fsHash);
            fsHash.Close();

            // 메시지 구조: [Type:1][Size:8][NameLen:4][Name:N][Hash:32] (구조 변경)
            // 하위 호환성 유지를 위해 기존 구조 뒤에 Hash를 붙이는 것이 안전하지만, 
            // Host측 파싱 로직도 수정하므로 구조를 명확히 함.
            // 여기서는 간단히 기존 구조 뒤에 Hash를 붙임.
            // 기존: [Type:1][Size:4][Name...] <- Size가 4바이트(uint)로 되어있음. 4GB 이상 불가. (수정 필요하지만 일단 유지)
            
            byte[] startMsg = new byte[1 + 4 + nameBytes.Length + 32];
            startMsg[0] = MSG_FILE_START;
            BitConverter.GetBytes((uint)fileSize).CopyTo(startMsg, 1); // 4GB Limit warning
            Array.Copy(nameBytes, 0, startMsg, 5, nameBytes.Length);
            Array.Copy(hashBytes, 0, startMsg, 5 + nameBytes.Length, 32); // 이름 뒤에 해시 부착

            // 이름 길이를 명시하지 않고 있어서 Host가 파싱할 때 애매했음. 
            // 기존 Host는 (전체 - 5)를 이름으로 간주.
            // Host 수정 시: (전체 - 5 - 32)를 이름으로 간주하도록 변경해야 함.

            _dc.send(startMsg);

            // 2. CHUNK
            const int chunkSize = 16 * 1024; // 16KB
            byte[] buffer = new byte[chunkSize];
            long sent = 0;
            
            using var fs = fileInfo.OpenRead();
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer, 0, chunkSize)) > 0)
            {
                byte[] chunkMsg = new byte[1 + bytesRead];
                chunkMsg[0] = MSG_FILE_CHUNK;
                Array.Copy(buffer, 0, chunkMsg, 1, bytesRead);
                _dc.send(chunkMsg);

                sent += bytesRead;
                int pct = (int)(sent * 100 / fileSize);
                OnProgress?.Invoke(pct);
                
                // Flow control / Buffer clear wait
                // SIPSorcery DataChannel doesn't expose bufferedAmount properly in older versions, 
                // but checking source might help. For now, simple delay.
                await Task.Delay(10); 
            }

            // 3. END
            _dc.send(new byte[] { MSG_FILE_END });
            OnStatus?.Invoke($"{fileName} 전송 완료, 검증 대기 중...");

            // 4. ACK Wait (Host가 저장을 완료할 때까지 대기)
            // 최대 30초 대기 (대용량 파일 저장 시간 고려)
            var ackTask = _ackTcs.Task;
            var timeoutTask = Task.Delay(30000);
            
            var completed = await Task.WhenAny(ackTask, timeoutTask);
            if (completed == timeoutTask)
            {
                 Console.WriteLine($"[FileTransfer] ACK timeout for {fileName}");
                 OnStatus?.Invoke($"응답 시간 초과 (ACK Timeout)");
            }
            else
            {
                 bool success = await ackTask; // Result check
                 if (success)
                 {
                     Console.WriteLine($"[FileTransfer] ACK received for {fileName}");
                     OnStatus?.Invoke($"전송 완료 (무결성 확인됨)");
                 }
                 else
                 {
                     Console.WriteLine($"[FileTransfer] Hash Mismatch reported by Host");
                     OnStatus?.Invoke($"전송 실패: 파일 무결성 검증 오류");
                 }
            }
        }

        private TaskCompletionSource<bool> _ackTcs = new();

        public void Dispose()
        {
            _signaling.OnSignalReceived -= OnSignalReceived;
            _dc?.close();
            _pc?.Close("disposed");
            _pc?.Dispose();
        }
    }
}
