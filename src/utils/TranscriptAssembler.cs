using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranscriber.utils
{
    public sealed partial class TranscriptAssembler
    {
        private const int MinimumAnchorCharacters = 4;

        private static readonly HashSet<string> Abbreviations = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs",
            "etc", "e.g", "i.e"
        };

        private readonly int safetySentenceCount;
        private string document = string.Empty;
        private string previousWindow = string.Empty;
        private int windowStart;
        private int committedLength;

        public TranscriptAssembler(int safetySentenceCount = 2)
        {
            if (safetySentenceCount < 1)
                throw new ArgumentOutOfRangeException(nameof(safetySentenceCount));
            this.safetySentenceCount = safetySentenceCount;
        }

        public string FullText => document;
        public string CommittedText => document[..Math.Min(committedLength, document.Length)].Trim();
        public string PendingText => document[Math.Min(committedLength, document.Length)..].Trim();

        public TranscriptUpdate Apply(string snapshot)
        {
            string currentWindow = Normalize(snapshot);
            if (string.IsNullOrEmpty(currentWindow))
                return new TranscriptUpdate(document, string.Empty, false);

            bool safetyViolation = false;

            if (string.IsNullOrEmpty(previousWindow))
            {
                document = currentWindow;
                previousWindow = currentWindow;
                windowStart = 0;
            }
            else
            {
                var anchor = FindLongestTokenAnchor(previousWindow, currentWindow);
                if (anchor.CharacterLength < MinimumAnchorCharacters)
                {
                    if (committedLength == 0)
                    {
                        document = currentWindow;
                        windowStart = 0;
                    }
                    else
                    {
                        document = Join(document[..committedLength], currentWindow);
                        windowStart = committedLength + SeparatorLength(document[..committedLength], currentWindow);
                    }
                }
                else
                {
                    int newWindowStart = Math.Clamp(
                        windowStart + anchor.PreviousCharacterIndex - anchor.CurrentCharacterIndex,
                        0,
                        document.Length);
                    int prefixLength = Math.Min(newWindowStart, document.Length);
                    string candidate = document[..prefixLength] + currentWindow;

                    if (committedLength > 0 &&
                        (candidate.Length < committedLength ||
                         !candidate.AsSpan(0, committedLength)
                             .SequenceEqual(document.AsSpan(0, committedLength))))
                    {
                        safetyViolation = true;
                        int currentOffset = Math.Clamp(
                            committedLength - newWindowStart,
                            0,
                            currentWindow.Length);
                        document = Join(
                            document[..committedLength],
                            currentWindow[currentOffset..]);
                    }
                    else
                    {
                        document = candidate;
                    }

                    windowStart = newWindowStart;
                }

                previousWindow = currentWindow;
            }

            int safeBoundary = FindSafeBoundary(document, safetySentenceCount);
            if (safeBoundary < committedLength)
            {
                safetyViolation = true;
                safeBoundary = committedLength;
            }

            string newlyCommitted = string.Empty;
            if (safeBoundary > committedLength)
            {
                newlyCommitted = document[committedLength..safeBoundary].Trim();
                committedLength = safeBoundary;
            }

            return new TranscriptUpdate(document, newlyCommitted, safetyViolation);
        }

        public string FlushPending()
        {
            string pending = PendingText;
            committedLength = document.Length;
            return pending;
        }

        public void Reset()
        {
            document = string.Empty;
            previousWindow = string.Empty;
            windowStart = 0;
            committedLength = 0;
        }

        private static string Normalize(string text)
        {
            return Whitespace().Replace(text, " ").Trim();
        }

        private static string Join(string prefix, string suffix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return suffix.TrimStart();
            if (string.IsNullOrWhiteSpace(suffix))
                return prefix.TrimEnd();
            return prefix.TrimEnd() + " " + suffix.TrimStart();
        }

        private static int SeparatorLength(string prefix, string suffix)
        {
            return string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(suffix) ? 0 : 1;
        }

        private static int FindSafeBoundary(string text, int protectedSentenceCount)
        {
            var boundaries = new List<int>();
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] is not ('.' or '!' or '?'))
                    continue;

                int punctuationStart = index;
                while (index + 1 < text.Length && text[index + 1] is '.' or '!' or '?')
                    index++;

                int boundaryEnd = index + 1;
                while (boundaryEnd < text.Length && text[boundaryEnd] is '"' or '\'' or '”' or '’' or ')' or ']')
                    boundaryEnd++;

                if (boundaryEnd < text.Length && !char.IsWhiteSpace(text[boundaryEnd]))
                    continue;
                if (text[punctuationStart] == '.' && IsAbbreviation(text, punctuationStart))
                    continue;

                while (boundaryEnd < text.Length && char.IsWhiteSpace(text[boundaryEnd]))
                    boundaryEnd++;
                boundaries.Add(boundaryEnd);
            }

            int commitBoundaryIndex = boundaries.Count - protectedSentenceCount - 1;
            return commitBoundaryIndex >= 0 ? boundaries[commitBoundaryIndex] : 0;
        }

        private static bool IsAbbreviation(string text, int periodIndex)
        {
            int wordStart = periodIndex - 1;
            while (wordStart >= 0 && (char.IsLetter(text[wordStart]) || text[wordStart] == '.'))
                wordStart--;
            string word = text[(wordStart + 1)..periodIndex].Trim('.');
            return Abbreviations.Contains(word) ||
                   (word.Length == 1 && char.IsUpper(word[0]));
        }

        private static TokenAnchor FindLongestTokenAnchor(string previous, string current)
        {
            var previousTokens = Tokenize(previous);
            var currentTokens = Tokenize(current);
            if (previousTokens.Count == 0 || currentTokens.Count == 0)
                return default;

            int[] priorRow = new int[currentTokens.Count + 1];
            int[] row = new int[currentTokens.Count + 1];
            int bestLength = 0;
            int bestPreviousEnd = 0;
            int bestCurrentEnd = 0;

            for (int previousIndex = 1; previousIndex <= previousTokens.Count; previousIndex++)
            {
                Array.Clear(row);
                for (int currentIndex = 1; currentIndex <= currentTokens.Count; currentIndex++)
                {
                    if (!string.Equals(
                            previousTokens[previousIndex - 1].Value,
                            currentTokens[currentIndex - 1].Value,
                            StringComparison.Ordinal))
                        continue;

                    row[currentIndex] = priorRow[currentIndex - 1] + 1;
                    if (row[currentIndex] > bestLength)
                    {
                        bestLength = row[currentIndex];
                        bestPreviousEnd = previousIndex;
                        bestCurrentEnd = currentIndex;
                    }
                }
                (priorRow, row) = (row, priorRow);
            }

            if (bestLength == 0)
                return default;

            int previousStartToken = bestPreviousEnd - bestLength;
            int currentStartToken = bestCurrentEnd - bestLength;
            int previousCharacterIndex = previousTokens[previousStartToken].Index;
            int currentCharacterIndex = currentTokens[currentStartToken].Index;
            int previousCharacterEnd = previousTokens[bestPreviousEnd - 1].End;

            return new TokenAnchor(
                previousCharacterIndex,
                currentCharacterIndex,
                previousCharacterEnd - previousCharacterIndex);
        }

        private static List<TokenSpan> Tokenize(string text)
        {
            return Tokens().Matches(text)
                .Select(match => new TokenSpan(match.Value, match.Index, match.Index + match.Length))
                .ToList();
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        [GeneratedRegex(@"[\p{L}\p{N}_]+(?:['’][\p{L}\p{N}_]+)*|[^\s]")]
        private static partial Regex Tokens();

        private readonly record struct TokenSpan(string Value, int Index, int End);
        private readonly record struct TokenAnchor(
            int PreviousCharacterIndex,
            int CurrentCharacterIndex,
            int CharacterLength);
    }

    public readonly record struct TranscriptUpdate(
        string FullText,
        string NewlyCommittedText,
        bool SafetyViolation);
}
