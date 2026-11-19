using System.Text;

namespace LiveCaptionsTranscriber.utils
{
    public readonly record struct TranscriptBoundary(int CharacterPosition, string Marker);

    public static class TranscriptDocumentFormatter
    {
        private static readonly string SectionSeparator =
            Environment.NewLine + Environment.NewLine;

        public static string Format(
            string transcript,
            IReadOnlyList<TranscriptBoundary> boundaries)
        {
            if (boundaries.Count == 0)
                return TranscriptParagraphFormatter.Format(transcript);

            var document = new StringBuilder();
            int sectionStart = 0;

            foreach (TranscriptBoundary boundary in boundaries)
            {
                int position = Math.Clamp(
                    boundary.CharacterPosition,
                    sectionStart,
                    transcript.Length);
                AppendSection(document, transcript[sectionStart..position]);
                AppendBlock(document, boundary.Marker);
                sectionStart = position;
            }

            AppendSection(document, transcript[sectionStart..]);
            return document.ToString();
        }

        private static void AppendSection(StringBuilder document, string text)
        {
            string formatted = TranscriptParagraphFormatter.Format(text);
            if (formatted.Length > 0)
                AppendBlock(document, formatted);
        }

        private static void AppendBlock(StringBuilder document, string block)
        {
            if (document.Length > 0)
                document.Append(SectionSeparator);
            document.Append(block.Trim());
        }
    }
}
