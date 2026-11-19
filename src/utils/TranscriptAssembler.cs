using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranscriber.utils
{
    public sealed partial class TranscriptAssembler
    {
        private const int MinimumAnchorCharacters = 12;

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
        private bool appendNextWindow;

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
                if (appendNextWindow && !string.IsNullOrEmpty(document))
                {
                    int separatorLength = SeparatorLength(document, currentWindow);
                    windowStart = document.Length + separatorLength;
                    document = Join(document, currentWindow);
                }
                else
                {
                    document = currentWindow;
                    windowStart = 0;
                }

                previousWindow = currentWindow;
                appendNextWindow = false;
            }
            else
            {
                var anchor = FindLongestCharacterAnchor(previousWindow, currentWindow);
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

        public string StartNewSegment()
        {
            string pending = FlushPending();
            previousWindow = string.Empty;
            windowStart = document.Length;
            appendNextWindow = !string.IsNullOrEmpty(document);
            return pending;
        }

        public void Reset()
        {
            document = string.Empty;
            previousWindow = string.Empty;
            windowStart = 0;
            committedLength = 0;
            appendNextWindow = false;
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

        private static CharacterAnchor FindLongestCharacterAnchor(string previous, string current)
        {
            if (previous.Length == 0 || current.Length == 0)
                return default;

            var states = BuildSuffixAutomaton(previous);
            int stateIndex = 0;
            int currentLength = 0;
            int bestLength = 0;
            int bestCurrentEnd = 0;
            int bestStateIndex = 0;

            for (int currentIndex = 0; currentIndex < current.Length; currentIndex++)
            {
                char value = current[currentIndex];
                while (stateIndex != 0 && !states[stateIndex].Transitions.ContainsKey(value))
                {
                    stateIndex = states[stateIndex].Link;
                    currentLength = Math.Min(currentLength, states[stateIndex].Length);
                }

                if (states[stateIndex].Transitions.TryGetValue(value, out int nextState))
                {
                    stateIndex = nextState;
                    currentLength++;
                }
                else
                {
                    stateIndex = 0;
                    currentLength = 0;
                }

                if (currentLength > bestLength)
                {
                    bestLength = currentLength;
                    bestCurrentEnd = currentIndex;
                    bestStateIndex = stateIndex;
                }
            }

            if (bestLength == 0)
                return default;

            bestStateIndex = FindStateForLength(states, bestStateIndex, bestLength);
            int previousEnd = states[bestStateIndex].FirstPosition;

            return new CharacterAnchor(
                previousEnd - bestLength + 1,
                bestCurrentEnd - bestLength + 1,
                bestLength);
        }

        private static List<SuffixState> BuildSuffixAutomaton(string text)
        {
            var states = new List<SuffixState>(text.Length * 2)
            {
                new() { Link = -1 }
            };
            int last = 0;

            for (int position = 0; position < text.Length; position++)
            {
                char value = text[position];
                int current = states.Count;
                states.Add(new SuffixState
                {
                    Length = states[last].Length + 1,
                    FirstPosition = position
                });

                int parent = last;
                while (parent >= 0 && !states[parent].Transitions.ContainsKey(value))
                {
                    states[parent].Transitions[value] = current;
                    parent = states[parent].Link;
                }

                if (parent < 0)
                {
                    states[current].Link = 0;
                }
                else
                {
                    int target = states[parent].Transitions[value];
                    if (states[parent].Length + 1 == states[target].Length)
                    {
                        states[current].Link = target;
                    }
                    else
                    {
                        int clone = states.Count;
                        states.Add(new SuffixState
                        {
                            Length = states[parent].Length + 1,
                            Link = states[target].Link,
                            FirstPosition = states[target].FirstPosition,
                            Transitions = new Dictionary<char, int>(states[target].Transitions)
                        });

                        while (parent >= 0 &&
                               states[parent].Transitions.TryGetValue(value, out int linked) &&
                               linked == target)
                        {
                            states[parent].Transitions[value] = clone;
                            parent = states[parent].Link;
                        }

                        states[target].Link = clone;
                        states[current].Link = clone;
                    }
                }

                last = current;
            }

            return states;
        }

        private static int FindStateForLength(
            IReadOnlyList<SuffixState> states,
            int stateIndex,
            int length)
        {
            while (states[stateIndex].Link >= 0 &&
                   states[states[stateIndex].Link].Length >= length)
                stateIndex = states[stateIndex].Link;
            return stateIndex;
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        private readonly record struct CharacterAnchor(
            int PreviousCharacterIndex,
            int CurrentCharacterIndex,
            int CharacterLength);

        private sealed class SuffixState
        {
            public int Length { get; set; }
            public int Link { get; set; }
            public int FirstPosition { get; set; }
            public Dictionary<char, int> Transitions { get; set; } = new();
        }
    }

    public readonly record struct TranscriptUpdate(
        string FullText,
        string NewlyCommittedText,
        bool SafetyViolation);
}
