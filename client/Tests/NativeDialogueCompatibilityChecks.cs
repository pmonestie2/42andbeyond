using System;
using System.IO;
using System.Linq;
using System.Text;
using FortyTwoAndBeyond.Protocol;

/// <summary>Verify native C-string rescue trees without accepting arbitrary trailing dialogue data.</summary>
internal static class NativeDialogueCompatibilityChecks
{
    /// <summary>Decode ordinary and sizeof(C-string) dialogue variants with strict branch boundaries.</summary>
    public static void Check()
    {
        byte[] ordinary = RescueTree();
        byte[] nativeLiteral = ordinary.Concat(new byte[] { 0 }).ToArray();
        var original = new DialogueState(0);
        DialogueState regular = original.WithTree(ordinary), rescue = original.WithTree(nativeLiteral);
        Require(regular.Text == rescue.Text && rescue.Choices.Select(choice => choice.Node).SequenceEqual(new byte[] { 0, 1, 2 }) &&
            rescue.Choices[0].Text == "Request tow" && rescue.Choices[2].Text == "Remain here" && original.Choices.Length == 0,
            "Native rescue string terminator changed choices or mutated an earlier dialogue.");
        var world = new World();
        world.Apply(new Frame(0x54, nativeLiteral), 91);
        Require(world.Station == null && world.Dialogue?.NpcId == 0 && world.Dialogue.Choices.Length == 3,
            "Native incapacitation response did not open the space rescue dialogue.");
        world.StartDialogue(25);
        world.Apply(new Frame(0x54, ordinary), 91);
        Require(world.Dialogue.NpcId == 25 && world.Dialogue.Choices[1].Node == 1,
            "Rescue compatibility broke normal station dialogue identity.");
        Reject(ordinary.Concat(new byte[] { 1 }).ToArray());
        Reject(ordinary.Concat(new byte[] { 0, 0 }).ToArray());
        Reject(nativeLiteral.Concat(new byte[] { 9 }).ToArray());
        Reject(ordinary.Take(ordinary.Length - 1).ToArray());
        byte[] malformedHeader = (byte[])ordinary.Clone(); malformedHeader[Array.IndexOf(malformedHeader, (byte)0) + 1] = 1;
        Reject(malformedHeader);
        byte[] tooMany = (byte[])ordinary.Clone(); tooMany[Array.IndexOf(tooMany, (byte)0) + 2] = 21;
        Reject(tooMany);
        byte[] oversizedNode = (byte[])ordinary.Clone(); oversizedNode[Array.IndexOf(oversizedNode, (byte)0) + 4] = 1;
        Reject(oversizedNode);
    }

    /// Returns:
    ///     int: Complete native rescue frames validated from a private capture, without copying captured data.
    public static int CheckCapturedRescue(string path)
    {
        int rescues = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new BinaryReader(stream))
        {
            long completePrefix = stream.Length;
            while (completePrefix - stream.Position >= 10)
            {
                uint owner = reader.ReadUInt32(); ushort opcode = reader.ReadUInt16(); uint length = reader.ReadUInt32();
                if (length > 32763) throw new InvalidDataException("Captured inner frame exceeds its native bound.");
                if (completePrefix - stream.Position < length) break; // The writer may still be appending the last record.
                byte[] body = reader.ReadBytes((int)length);
                if (opcode != 0x54) continue;
                var world = new World(); world.Apply(new Frame(opcode, body), owner);
                Require(world.Dialogue != null, "Captured talk tree did not publish a dialogue.");
                if (world.Dialogue.Choices.Select(choice => choice.Node).SequenceEqual(new byte[] { 0, 1, 2 }) &&
                    body.Length > 1 && body[body.Length - 1] == 0 && body[body.Length - 2] == 0)
                    rescues++;
            }
        }
        Require(rescues > 0, "The private capture contained no complete native sizeof-string rescue tree.");
        return rescues;
    }

    /// Returns:
    ///     byte[]: A synthetic standard tree matching the native rescue branch encoding.
    private static byte[] RescueTree() => Wire.Body(writer =>
    {
        WriteText(writer, "Synthetic rescue options"); writer.Write((byte)0); writer.Write((byte)3);
        writer.Write(0u); WriteText(writer, "Request tow");
        writer.Write(1u); WriteText(writer, "Distress beacon");
        writer.Write(2u); WriteText(writer, "Remain here");
    });

    /// <summary>Write a bounded fixture string with its native NUL terminator.</summary>
    private static void WriteText(BinaryWriter writer, string text)
    { writer.Write(Encoding.UTF8.GetBytes(text)); writer.Write((byte)0); }

    /// <summary>Require malformed native tree variants to remain protocol errors.</summary>
    private static void Reject(byte[] body)
    {
        try { new DialogueState(0).WithTree(body); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed native dialogue tree was accepted.");
    }

    /// <summary>Fail with the dialogue compatibility contract that regressed.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
