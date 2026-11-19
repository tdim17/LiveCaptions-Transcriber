using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranscriber.utils
{
    public static partial class TranscriptParagraphFormatter
    {
        private const int DefaultSentencesPerParagraph = 3;
        private const int DefaultTargetParagraphCharacters = 500;

        private static readonly HashSet<string> CommonAbbreviations = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs",
            "etc", "e.g", "i.e"
        };

        public static string Format(
            string text,
            int sentencesPerParagraph = DefaultSentencesPerParagraph,
            int targetParagraphCharacters = DefaultTargetParagraphCharacters)
        {
            if (sentencesPerParagraph < 1)
                throw new ArgumentOutOfRangeException(nameof(sentencesPerParagraph));
            if (targetParagraphCharacters < 1)
                throw new ArgumentOutOfRangeException(nameof(targetParagraphCharacters));

            string normalized = Whitespace().Replace(text, " ").Trim();
            if (normalized.Length == 0)
                return string.Empty;

            var segments = SplitIntoSegments(normalized);
            var paragraphs = new List<string>();
            var paragraph = new StringBuilder();
            int completedSentences = 0;

            foreach (var segment in segments)
            {
                if (paragraph.Length > 0)
                    paragraph.Append(' ');
                paragraph.Append(segment.Text);

                if (!segment.IsComplete)
                    continue;

                completedSentences++;
                if (completedSentences >= sentencesPerParagraph ||
                    paragraph.Length >= targetParagraphCharacters)
                {
                    paragraphs.Add(paragraph.ToString());
                    paragraph.Clear();
                    completedSentences = 0;
                }
            }

            if (paragraph.Length > 0)
                paragraphs.Add(paragraph.ToString());

            return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
        }

        private static List<TextSegment> SplitIntoSegments(string text)
        {
            var result = new List<TextSegment>();
            int segmentStart = 0;

            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] is not ('.' or '!' or '?'))
                    continue;

                int punctuationStart = index;
                while (index + 1 < text.Length && text[index + 1] is '.' or '!' or '?')
                    index++;

                int boundaryEnd = index + 1;
                while (boundaryEnd < text.Length &&
                       text[boundaryEnd] is '"' or '\'' or '”' or '’' or ')' or ']')
                    boundaryEnd++;

                if (boundaryEnd < text.Length && !char.IsWhiteSpace(text[boundaryEnd]))
                    continue;
                if (text[punctuationStart] == '.' && IsAbbreviation(text, punctuationStart))
                    continue;

                result.Add(new TextSegment(
                    text[segmentStart..boundaryEnd].Trim(),
                    true));
                while (boundaryEnd < text.Length && char.IsWhiteSpace(text[boundaryEnd]))
                    boundaryEnd++;
                segmentStart = boundaryEnd;
                index = boundaryEnd - 1;
            }

            if (segmentStart < text.Length)
                result.Add(new TextSegment(text[segmentStart..].Trim(), false));

            return result;
        }

        private static bool IsAbbreviation(string text, int periodIndex)
        {
            int wordStart = periodIndex - 1;
            while (wordStart >= 0 && (char.IsLetter(text[wordStart]) || text[wordStart] == '.'))
                wordStart--;

            string word = text[(wordStart + 1)..periodIndex].Trim('.');
            if (CommonAbbreviations.Contains(word) ||
                (word.Length == 1 && char.IsUpper(word[0])))
                return true;

            string[] initialParts = word.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return initialParts.Length > 1 && initialParts.All(part => part.Length == 1);
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        private readonly record struct TextSegment(string Text, bool IsComplete);
    }
}
