using System;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    public sealed partial class LocalSession
    {
        private BinaryWriter packetJournal;
        private StreamWriter transportJournal;

        /// <summary>Consume an explicitly armed one-session capture request after authentication.</summary>
        private void StartPacketJournal()
        {
            string directory = Path.Combine(Path.GetTempPath(), "42andbeyond-packet-capture");
            string request = Path.Combine(directory, "capture-next-session");
            if (!File.Exists(request)) return;
            try
            {
                string claimed = request + "." + Guid.NewGuid().ToString("N");
                File.Move(request, claimed);
                File.Delete(claimed);
                string path = Path.Combine(directory, "sector-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") +
                    "-" + Guid.NewGuid().ToString("N") + ".bin");
                packetJournal = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                transportJournal = new StreamWriter(new FileStream(path + ".transport.log",
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Record bounded transport headers and repair progress without authentication payloads.</summary>
        private void RecordTransport(string direction, ushort opcode, int size, uint packetSequence)
        {
            if (transportJournal == null) return;
            RecordTransportLine(direction + " opcode=" + opcode.ToString("x4") + " bytes=" + size +
                " sequence=" + packetSequence + " expected=" + (stream?.Expected ?? 0) +
                " missing=" + (stream?.MissingCount ?? 0) + " ready=" + Ready);
        }

        /// <summary>Record exact motion datagrams within the armed session's bounded private journal.</summary>
        private void RecordMotionPacket(byte[] packet)
        {
            if (transportJournal == null) return;
            RecordTransportLine("sent-motion1004 endpoint=" + comms.Client.RemoteEndPoint +
                " bytes=" + packet.Length + " hex=" + BitConverter.ToString(packet).Replace("-", ""));
        }

        /// <summary>Keep transport and optional motion diagnostics within the private two-megabyte journal.</summary>
        private void RecordTransportLine(string detail)
        {
            if (transportJournal == null) return;
            try
            {
                if (transportJournal.BaseStream.Position > 2 * 1024 * 1024)
                { transportJournal.Dispose(); transportJournal = null; return; }
                transportJournal.WriteLine(clock.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " " + detail);
                transportJournal.Flush();
            }
            catch (IOException) { transportJournal.Dispose(); transportJournal = null; }
        }

        /// <summary>Record bounded decoded sector packets only; account authentication is never recorded.</summary>
        private void RecordSectorFrame(Frame frame)
        {
            if (packetJournal == null) return;
            try
            {
                if (packetJournal.BaseStream.Position + frame.Body.Length + 10 > 8 * 1024 * 1024)
                { packetJournal.Dispose(); packetJournal = null; return; }
                packetJournal.Write(Player); packetJournal.Write(frame.Opcode);
                packetJournal.Write(frame.Body.Length); packetJournal.Write(frame.Body);
                packetJournal.Flush();
            }
            catch (IOException) { packetJournal.Dispose(); packetJournal = null; }
        }
    }
}
