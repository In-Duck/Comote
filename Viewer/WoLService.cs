using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Viewer
{
    public class WoLService
    {
        public static void SendMagicPacket(string macAddress)
        {
            try
            {
                byte[] macBytes = ParseMacAddress(macAddress);
                byte[] magicPacket = CreateMagicPacket(macBytes);

                // 브로드캐스트 전송 (Port 9)
                using (UdpClient client = new UdpClient())
                {
                    client.EnableBroadcast = true;
                    // 로컬 네트워크 브로드캐스트: 255.255.255.255
                    IPEndPoint endPoint = new IPEndPoint(IPAddress.Broadcast, 9); 
                    client.Send(magicPacket, magicPacket.Length, endPoint);
                    Console.WriteLine($"[WoL] Magic packet sent to {macAddress}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WoL] Failed to send magic packet: {ex.Message}");
            }
        }

        private static byte[] ParseMacAddress(string macAddress)
        {
            var CleanMac = macAddress.Replace(":", "").Replace("-", "");
            if (CleanMac.Length != 12)
                throw new ArgumentException("Invalid MAC address length");

            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                macBytes[i] = Convert.ToByte(CleanMac.Substring(i * 2, 2), 16);
            }
            return macBytes;
        }

        private static byte[] CreateMagicPacket(byte[] macBytes)
        {
            byte[] packet = new byte[6 + 16 * 6];

            // 헤더: FF FF FF FF FF FF
            for (int i = 0; i < 6; i++)
            {
                packet[i] = 0xFF;
            }

            // 바디: MAC 주소 16회 반복
            for (int i = 0; i < 16; i++)
            {
                Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
            }

            return packet;
        }
    }
}
