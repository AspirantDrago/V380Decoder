using System.Security.Cryptography;

namespace V380Decoder.src
{
    internal sealed class AudioPayloadDecoder(byte[] aesKey)
    {
        private (int header, bool decrypt)? pending;
        private (int header, bool decrypt)? selected;
        public int HeaderSize => selected?.header ?? pending?.header ?? 0;
        public bool Decrypts => selected?.decrypt ?? pending?.decrypt ?? false;
        public bool Confirmed => selected.HasValue;

        public void Reset() { pending = null; selected = null; }

        public bool TryDecodeIma(byte[] frame, bool encrypted, out byte[] alaw, out bool changed)
        {
            alaw = Array.Empty<byte>();
            changed = false;
            if (selected.HasValue &&
                TryCandidate(frame, selected.Value.header, selected.Value.decrypt, out var confirmed))
            {
                alaw = DecodeBlocks(confirmed);
                return true;
            }

            selected = null;
            foreach (int header in new[] { 16, 18, 20 })
            {
                foreach (bool decrypt in encrypted ? new[] { true, false } : new[] { false })
                {
                    if (!TryCandidate(frame, header, decrypt, out var data)) continue;
                    var candidate = (header, decrypt);
                    // An accidental valid state header in ciphertext must not become audio.
                    // Require the same layout on two consecutive frames before emitting it.
                    if (pending == candidate)
                    {
                        selected = candidate;
                        changed = true;
                        alaw = DecodeBlocks(data);
                        return true;
                    }
                    pending = candidate;
                    return false;
                }
            }
            pending = null;
            return false;
        }

        private bool TryCandidate(byte[] frame, int header, bool decrypt, out byte[] data)
        {
            data = Array.Empty<byte>();
            int length = frame.Length - header;
            // V380 IMA/DVI mono blocks are 256 bytes (505 samples at 8 kHz).
            // Only accept complete blocks, never guess that an arbitrary 0x16 is PCMA.
            if (length <= 0 || length % 256 != 0) return false;
            data = frame[header..];
            if (decrypt)
            {
                using var aes = Aes.Create();
                aes.Key = aesKey;
                data = aes.DecryptEcb(data, PaddingMode.None);
            }
            for (int i = 0; i < data.Length; i += 256)
                if (!ImaAdpcm.HasValidHeader(data.AsSpan(i, 256))) return false;
            return true;
        }

        private static byte[] DecodeBlocks(byte[] data)
        {
            var output = new byte[data.Length / 256 * 505];
            for (int i = 0, offset = 0; i < data.Length; i += 256, offset += 505)
                ImaAdpcm.DecodeToAlaw(data.AsSpan(i, 256)).CopyTo(output, offset);
            return output;
        }
    }
}
