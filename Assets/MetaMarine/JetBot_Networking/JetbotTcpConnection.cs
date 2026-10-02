using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MetaMarine.VR
{
    // Unity API를 호출하지 않는 통신 전용 작업자.
    // 메인 스레드가 만든 JSON을 순서대로 보내고 ACK 문자열만 돌려준다.
    internal sealed class JetbotTcpConnection
    {
        readonly BlockingCollection<byte[]> pending = new BlockingCollection<byte[]>();
        readonly ConcurrentQueue<string> acks = new ConcurrentQueue<string>();
        readonly string host;
        readonly int port;
        readonly Thread worker;
        volatile bool connected;
        volatile string fault = "";

        public bool Connected => connected;
        public string Fault => fault;
        public int PendingCount => pending.Count;

        public JetbotTcpConnection(string host, int port)
        {
            this.host = host;
            this.port = port;
            worker = new Thread(Run) { IsBackground = true, Name = "Jetbot TCP Sender" };
            worker.Start();
        }

        public bool Enqueue(byte[] json)
        {
            if (fault.Length != 0 || pending.IsAddingCompleted)
                return false;
            pending.Add(json);
            return true;
        }

        public bool TryReadAck(out string json) => acks.TryDequeue(out json);

        public void Complete()
        {
            // 정상 Disable에서는 goodbye까지 큐를 비운 뒤 연결을 닫는다.
            if (!pending.IsAddingCompleted)
                pending.CompleteAdding();
        }

        void Run()
        {
            TcpClient client = null;
            try
            {
                // 서버를 먼저 켜지 않아도 Unity 프레임을 막지 않고 연결을 기다린다.
                // 아직 성립하지 않은 연결만 재시도하며, 성립 후 단절은 자동 재송신하지 않는다.
                while (client == null)
                {
                    if (pending.IsAddingCompleted)
                        return;
                    var candidate = new TcpClient(AddressFamily.InterNetwork);
                    try
                    {
                        var attempt = candidate.ConnectAsync(IPAddress.Parse(host), port);
                        if (!attempt.Wait(1000))
                            throw new IOException("최초 연결 대기");
                        attempt.GetAwaiter().GetResult();
                        client = candidate;
                    }
                    catch (Exception)
                    {
                        candidate.Dispose();
                        Thread.Sleep(250);
                    }
                }
                client.NoDelay = true;
                connected = true;
                using (NetworkStream stream = client.GetStream())
                {
                    foreach (byte[] json in pending.GetConsumingEnumerable())
                    {
                        byte[] header = {
                            (byte)(json.Length >> 24), (byte)(json.Length >> 16),
                            (byte)(json.Length >> 8), (byte)json.Length
                        };
                        stream.Write(header, 0, 4);
                        stream.Write(json, 0, json.Length);
                        // 느린 네트워크에서도 샘플을 덮어쓰지 않는다.
                        // ACK 대기와 TCP 재송은 메인 스레드 밖에서 수행한다.
                        byte[] ackHeader = ReadExact(stream, 4);
                        int size = (ackHeader[0] << 24) | (ackHeader[1] << 16) |
                                   (ackHeader[2] << 8) | ackHeader[3];
                        if (size <= 0 || size > 16384)
                            throw new IOException("TCP ACK 크기 오류");
                        acks.Enqueue(Encoding.UTF8.GetString(ReadExact(stream, size)));
                    }
                }
            }
            catch (Exception error)
            {
                // 부분 전달 여부를 모르는 명령은 임의 재실행하지 않는다.
                fault = error.GetType().Name + ": " + error.Message;
            }
            finally
            {
                connected = false;
                client?.Dispose();
            }
        }

        static byte[] ReadExact(Stream stream, int size)
        {
            byte[] result = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int count = stream.Read(result, offset, size - offset);
                if (count == 0)
                    throw new IOException("TCP 연결 종료 / 미확인 명령 존재 가능");
                offset += count;
            }
            return result;
        }
    }
}
