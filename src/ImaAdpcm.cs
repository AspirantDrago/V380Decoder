using System.Buffers.Binary;

namespace V380Decoder.src
{
    // Mono IMA/DVI blocks: signed LE predictor, step index, reserved byte,
    // then low/high nibbles. Each block carries its own decoder state.
    internal static class ImaAdpcm
    {
        private static readonly int[] Steps =
        [
            7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,
            50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,
            253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,
            1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,
            3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,
            10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,
            27086,29794,32767
        ];
        private static readonly int[] IndexChanges = [-1,-1,-1,-1,2,4,6,8];

        public static bool HasValidHeader(ReadOnlySpan<byte> block) =>
            block.Length >= 5 && block[2] <= 88 && block[3] == 0;

        public static byte[] DecodeToAlaw(ReadOnlySpan<byte> block)
        {
            if (!HasValidHeader(block)) throw new ArgumentException("Invalid mono IMA block");
            int predictor = BinaryPrimitives.ReadInt16LittleEndian(block);
            int index = block[2];
            var samples = new byte[1 + (block.Length - 4) * 2];
            samples[0] = EncodeAlaw(predictor);
            int output = 1;
            for (int i = 4; i < block.Length; i++)
            {
                samples[output++] = DecodeNibble(block[i] & 15, ref predictor, ref index);
                samples[output++] = DecodeNibble(block[i] >> 4, ref predictor, ref index);
            }
            return samples;
        }

        private static byte DecodeNibble(int nibble, ref int predictor, ref int index)
        {
            int step = Steps[index];
            int delta = ((2 * (nibble & 7) + 1) * step) >> 3;
            predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -delta : delta), -32768, 32767);
            index = Math.Clamp(index + IndexChanges[nibble & 7], 0, 88);
            return EncodeAlaw(predictor);
        }

        internal static byte EncodeAlaw(int sample)
        {
            // Nearest G.711 reconstruction level, using the same input quantization in steps of four
            // as FFmpeg's pcm_alaw encoder.
            int magnitude = Math.Abs(sample >> 2);
            int low = 0, high = 127;
            while (low < high)
            {
                int middle = (low + high) / 2;
                int threshold = (AlawMagnitude(middle) + AlawMagnitude(middle + 1) + 4) >> 3;
                if (magnitude < threshold) high = middle;
                else low = middle + 1;
            }
            return (byte)(low ^ (sample >= 0 ? 0xD5 : 0x55));
        }

        private static int AlawMagnitude(int code)
        {
            int segment = code >> 4;
            int mantissa = code & 15;
            return segment == 0 ? (2 * mantissa + 1) << 3 :
                (2 * mantissa + 33) << (segment + 2);
        }
    }
}
