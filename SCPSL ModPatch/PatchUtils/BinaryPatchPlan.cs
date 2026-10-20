namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// Validates every write before changing the caller's buffer.
    /// </summary>
    internal sealed class BinaryPatchPlan
    {
        const byte NOP = 144;

        readonly byte[] _image;
        readonly List<Write> _writes = new List<Write>();

        public BinaryPatchPlan(byte[] image)
        {
            _image = image;
        }

        /// <summary>
        /// Adds a patch after validating its size, bounds and overlaps.
        /// </summary>
        public void Add(string name, int offset, int size, byte[] data)
        {
            if (data.Length == 0 || size <= 0 || size < data.Length)
                throw new InvalidDataException($"Patch ({name}) must have nonempty data and patchSize at least its data length.");
            if (offset < 0 || offset > _image.Length - size)
                throw new InvalidDataException($"Patch ({name}) extends outside the binary.");
            foreach (Write write in _writes)
            {
                if ((long)offset < (long)write.Offset + write.Size && (long)write.Offset < (long)offset + size)
                    throw new InvalidDataException($"Patch ({name}) overlaps patch ({write.Name}).");
            }
            _writes.Add(new Write(name, offset, size, (byte[])data.Clone()));
        }

        /// <summary>
        /// Applies the validated patches and fills unused patch bytes with NOP.
        /// </summary>
        public void Apply()
        {
            foreach (Write write in _writes)
            {
                Span<byte> target = _image.AsSpan(write.Offset, write.Size);
                target.Fill(NOP);
                write.Data.CopyTo(target);
            }
        }

        private sealed record Write(string Name, int Offset, int Size, byte[] Data);
    }
}
