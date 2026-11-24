namespace LiveCaptionsTranscriber.utils
{
    public sealed class TranscriptSessionDocument
    {
        private static readonly string BlockSeparator =
            Environment.NewLine + Environment.NewLine;

        private readonly TranscriptAssembler activeAssembler = new();
        private string frozenDocument = string.Empty;

        public string FrozenDocument => frozenDocument;
        public string ActiveText => activeAssembler.FullText;
        public string FullDocument => JoinBlocks(
            frozenDocument,
            TranscriptParagraphFormatter.Format(activeAssembler.FullText));

        public TranscriptUpdate ApplySnapshot(string snapshot)
        {
            return activeAssembler.Apply(snapshot);
        }

        public TranscriptFreezeResult FreezeActiveSegment()
        {
            string pending = activeAssembler.FlushPending();
            string formattedActive =
                TranscriptParagraphFormatter.Format(activeAssembler.FullText);
            frozenDocument = JoinBlocks(frozenDocument, formattedActive);
            activeAssembler.Reset();
            return new TranscriptFreezeResult(pending, frozenDocument);
        }

        public void ResumeWithBoundary(string marker, string ignoredSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(activeAssembler.FullText))
                throw new InvalidOperationException(
                    "The active transcript must be frozen before resuming.");

            frozenDocument = JoinBlocks(frozenDocument, marker);
            activeAssembler.Reset();
            activeAssembler.PrimeIgnoredWindow(ignoredSnapshot);
        }

        public void ReplaceFrozenDocument(string editedDocument)
        {
            if (!string.IsNullOrWhiteSpace(activeAssembler.FullText))
                throw new InvalidOperationException(
                    "The transcript can only be edited while no active segment exists.");

            frozenDocument = editedDocument.Trim();
        }

        public void Reset()
        {
            frozenDocument = string.Empty;
            activeAssembler.Reset();
        }

        private static string JoinBlocks(string prefix, string suffix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return suffix.Trim();
            if (string.IsNullOrWhiteSpace(suffix))
                return prefix.Trim();
            return prefix.TrimEnd() + BlockSeparator + suffix.TrimStart();
        }
    }

    public readonly record struct TranscriptFreezeResult(
        string PendingText,
        string Document);
}
