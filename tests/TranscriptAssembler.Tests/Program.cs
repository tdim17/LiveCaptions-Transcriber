using LiveCaptionsTranscriber.utils;


static void AssertEqual(string expected, string actual, string name)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"{name} failed.\nExpected: {expected}\nActual:   {actual}");
}

static void AssertTrue(bool value, string name)
{
    if (!value)
        throw new InvalidOperationException($"{name} failed.");
}

static void EditedFrozenTextSurvivesResume()
{
    var document = new TranscriptSessionDocument();
    document.ApplySnapshot("Original first block.");
    document.FreezeActiveSegment();
    document.ReplaceFrozenDocument("Manually corrected first block.");
    document.ResumeWithBoundary(
        "========== CONTINUE 2026-11-24 09:30:28 ==========",
        "Ignored pause material.");
    document.ApplySnapshot("Ignored pause material. New second block.");

    AssertTrue(
        document.FullDocument.Contains("Manually corrected first block.", StringComparison.Ordinal),
        nameof(EditedFrozenTextSurvivesResume) + " corrected");
    AssertTrue(
        !document.FullDocument.Contains("Original first block.", StringComparison.Ordinal),
        nameof(EditedFrozenTextSurvivesResume) + " original removed");
    AssertTrue(
        document.FullDocument.Contains("New second block.", StringComparison.Ordinal),
        nameof(EditedFrozenTextSurvivesResume) + " appended");
}

static void EarlierBlocksRemainEditableAfterSeveralResumes()
{
    var document = new TranscriptSessionDocument();
    document.ApplySnapshot("First block.");
    document.FreezeActiveSegment();
    document.ResumeWithBoundary("========== CONTINUE ONE ==========", string.Empty);
    document.ApplySnapshot("Second block.");
    document.FreezeActiveSegment();
    document.ResumeWithBoundary("========== CONTINUE TWO ==========", string.Empty);
    document.ApplySnapshot("Third block.");
    document.FreezeActiveSegment();

    document.ReplaceFrozenDocument(
        document.FrozenDocument.Replace(
            "First block.",
            "Corrected first block.",
            StringComparison.Ordinal));

    AssertTrue(
        document.FullDocument.Contains("Corrected first block.", StringComparison.Ordinal),
        nameof(EarlierBlocksRemainEditableAfterSeveralResumes));
    AssertTrue(
        document.FullDocument.Contains("Second block.", StringComparison.Ordinal),
        nameof(EarlierBlocksRemainEditableAfterSeveralResumes) + " second");
    AssertTrue(
        document.FullDocument.Contains("Third block.", StringComparison.Ordinal),
        nameof(EarlierBlocksRemainEditableAfterSeveralResumes) + " third");
}

static void TemporaryPeriodsAreReplaced()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("Italy.");
    assembler.Apply("Italy from.");
    assembler.Apply("Italy from the first.");
    assembler.Apply("Italy from the first day you took office. Next.");
    assembler.Apply("Italy from the first day you took office. Next sentence.");
    var update = assembler.Apply(
        "Italy from the first day you took office. Next sentence. Third sentence.");

    AssertEqual(
        "Italy from the first day you took office. Next sentence. Third sentence.",
        update.FullText,
        nameof(TemporaryPeriodsAreReplaced));
    AssertEqual(string.Empty, update.NewlyCommittedText,
        nameof(TemporaryPeriodsAreReplaced) + " committed text");
}

static void RollingWindowKeepsDroppedHistory()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("One complete. Two complete. Three current.");
    var update = assembler.Apply("Two complete. Three current. Four current.");

    AssertEqual(
        "One complete. Two complete. Three current. Four current.",
        update.FullText,
        nameof(RollingWindowKeepsDroppedHistory));
    AssertEqual(
        "One complete.",
        update.NewlyCommittedText,
        nameof(RollingWindowKeepsDroppedHistory) + " committed text");
}

static void PreviousSentenceCanStillChange()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("One stable. Second draft. Third draft.");
    var update = assembler.Apply("One stable. Second corrected. Third draft.");

    AssertEqual(
        "One stable. Second corrected. Third draft.",
        update.FullText,
        nameof(PreviousSentenceCanStillChange));
    AssertTrue(!update.SafetyViolation, nameof(PreviousSentenceCanStillChange) + " safety");
}

static void VisibleSentencesStayMutable()
{
    var assembler = new TranscriptAssembler();
    var update = assembler.Apply("Mr. Smith arrived. Second sentence. Third sentence.");

    AssertEqual(string.Empty, update.NewlyCommittedText, nameof(VisibleSentencesStayMutable));
}

static void ChangesBeyondSafetyWindowAreReported()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("One sentence. Two sentence. Three sentence. Four sentence.");
    assembler.Apply("Two sentence. Three sentence. Four sentence.");
    var update = assembler.Apply(
        "One changed. Two sentence. Three sentence. Four sentence.");

    AssertTrue(update.SafetyViolation, nameof(ChangesBeyondSafetyWindowAreReported));
}

static void RestartKeepsPreviousSegment()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("Before restart. Still pending.");
    string flushed = assembler.StartNewSegment();
    var update = assembler.Apply("After restart. New window.");

    AssertEqual(
        "Before restart. Still pending.",
        flushed,
        nameof(RestartKeepsPreviousSegment) + " flushed text");
    AssertEqual(
        "Before restart. Still pending. After restart. New window.",
        update.FullText,
        nameof(RestartKeepsPreviousSegment));
}

static void PunctuationRevisionsDoNotShiftWordStarts()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply(
        "Evil was about to walk in that door. Four teenage girls, their frozen yogurt shop. " +
        "Just a tragedy. They were each shot, execution style, cold blooded. Who killed these girls");
    assembler.Apply(
        "Evil was about to walk in that door. Four teenage girls, their frozen yogurt shop. " +
        "Just a tragedy. They were each shot, execution style, cold blooded. Who killed these girls? " +
        "Wherever you listen to podcasts or listen ad free with twenty twenty true crime.");
    var update = assembler.Apply(
        "Evil was about to walk in that door. Four teenage girls, their frozen yogurt shop. " +
        "Just a tragedy. They were each shot execution style, cold blooded. Who killed these girls? " +
        "Wherever you listen to podcasts or listen ad free with twenty twenty true crime. " +
        "Plus you're watching America's number one streaming News Live.");

    AssertTrue(
        update.FullText.Contains("Who killed these girls", StringComparison.Ordinal),
        nameof(PunctuationRevisionsDoNotShiftWordStarts) + " who");
    AssertTrue(
        update.FullText.Contains("Plus you're watching", StringComparison.Ordinal),
        nameof(PunctuationRevisionsDoNotShiftWordStarts) + " plus");
}

static void LeadingPunctuationDoesNotMoveWindowStart()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("Either you know in meetings or email.");
    var update = assembler.Apply("Either, you know, in meetings or email.");

    AssertEqual(
        "Either, you know, in meetings or email.",
        update.FullText,
        nameof(LeadingPunctuationDoesNotMoveWindowStart));
    AssertTrue(!update.SafetyViolation,
        nameof(LeadingPunctuationDoesNotMoveWindowStart) + " safety");
}

static void WeakAnchorStartsAndThenUpdatesNewSegment()
{
    var assembler = new TranscriptAssembler();
    string firstSegment = string.Join(" ", Enumerable.Repeat("Earlier stable material.", 8));
    assembler.Apply(firstSegment);
    assembler.Apply("Has.");
    var update = assembler.Apply("Has been updated into a complete new segment.");

    AssertEqual(
        firstSegment + " Has been updated into a complete new segment.",
        update.FullText,
        nameof(WeakAnchorStartsAndThenUpdatesNewSegment));
}

static void FormatsReadableParagraphsWithoutChangingText()
{
    const string source =
        "One sentence. Two sentence. Three sentence. Four sentence. " +
        "Five sentence. Six sentence. Seven unfinished";
    string formatted = TranscriptParagraphFormatter.Format(source);
    string[] paragraphs = formatted.Split(
        Environment.NewLine + Environment.NewLine,
        StringSplitOptions.None);

    AssertEqual("3", paragraphs.Length.ToString(),
        nameof(FormatsReadableParagraphsWithoutChangingText) + " paragraph count");
    AssertEqual(source, formatted.Replace(Environment.NewLine + Environment.NewLine, " "),
        nameof(FormatsReadableParagraphsWithoutChangingText) + " text preservation");
}

static void DoesNotSplitCommonAbbreviations()
{
    const string source =
        "Dr. Smith works in the U.S. today. Second sentence. Third sentence.";
    string formatted = TranscriptParagraphFormatter.Format(source, 1, 500);
    string separator = Environment.NewLine + Environment.NewLine;

    AssertTrue(!formatted.Contains("Dr." + separator, StringComparison.Ordinal),
        nameof(DoesNotSplitCommonAbbreviations) + " title");
    AssertTrue(!formatted.Contains("U.S." + separator, StringComparison.Ordinal),
        nameof(DoesNotSplitCommonAbbreviations) + " initials");
    AssertEqual("3", formatted.Split(separator, StringSplitOptions.None).Length.ToString(),
        nameof(DoesNotSplitCommonAbbreviations) + " paragraph count");
}

static void ResumeMarkerIsAnIndependentFormattingBoundary()
{
    const string source =
        "Before pause first sentence. Before pause second sentence. " +
        "After resume first sentence. After resume second sentence.";
    int boundaryPosition = source.IndexOf("After resume", StringComparison.Ordinal);
    const string marker = "========== CONTINUE 2026-11-19 14:32:08 ==========";

    string formatted = TranscriptDocumentFormatter.Format(
        source,
        new[] { new TranscriptBoundary(boundaryPosition, marker) });
    string separator = Environment.NewLine + Environment.NewLine;

    AssertTrue(
        formatted.Contains("second sentence." + separator + marker + separator + "After resume"),
        nameof(ResumeMarkerIsAnIndependentFormattingBoundary));
    AssertEqual(
        source,
        formatted.Replace(separator + marker + separator, " "),
        nameof(ResumeMarkerIsAnIndependentFormattingBoundary) + " text preservation");
}

static void ResumeDiscardsExcludedIntervalAndCapturesOnlyNewText()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("Recorded before pause.");
    assembler.StartNewSegment();
    assembler.PrimeIgnoredWindow("Intentionally excluded first part. Excluded second part.");

    assembler.Apply(
        "Intentionally excluded first part. Excluded second part. New content begins.");
    var update = assembler.Apply(
        "Excluded second part. New content begins. More useful content.");

    AssertEqual(
        "Recorded before pause. New content begins. More useful content.",
        update.FullText,
        nameof(ResumeDiscardsExcludedIntervalAndCapturesOnlyNewText));
    AssertTrue(
        !update.FullText.Contains("excluded", StringComparison.OrdinalIgnoreCase),
        nameof(ResumeDiscardsExcludedIntervalAndCapturesOnlyNewText) + " excluded text");
}

static void ResumeWorksWithShortExcludedBaseline()
{
    var assembler = new TranscriptAssembler();
    assembler.Apply("Before pause.");
    assembler.StartNewSegment();
    assembler.PrimeIgnoredWindow("OK.");

    var update = assembler.Apply("OK. New content after resume.");

    AssertEqual(
        "Before pause. New content after resume.",
        update.FullText,
        nameof(ResumeWorksWithShortExcludedBaseline));
}

static void ResumePreservesVeryShortWantedPhrases()
{
    var firstAssembler = new TranscriptAssembler();
    firstAssembler.Apply("Before first pause.");
    firstAssembler.StartNewSegment();
    firstAssembler.PrimeIgnoredWindow("Excluded interval.");
    var firstUpdate = firstAssembler.Apply("Excluded interval. I see.");

    AssertEqual(
        "Before first pause. I see.",
        firstUpdate.FullText,
        nameof(ResumePreservesVeryShortWantedPhrases) + " I see");

    var secondAssembler = new TranscriptAssembler();
    secondAssembler.Apply("Before second pause.");
    secondAssembler.StartNewSegment();
    secondAssembler.PrimeIgnoredWindow("Another excluded interval.");
    var secondUpdate = secondAssembler.Apply("Another excluded interval. OK.");

    AssertEqual(
        "Before second pause. OK.",
        secondUpdate.FullText,
        nameof(ResumePreservesVeryShortWantedPhrases) + " OK");
}

TemporaryPeriodsAreReplaced();
RollingWindowKeepsDroppedHistory();
PreviousSentenceCanStillChange();
VisibleSentencesStayMutable();
ChangesBeyondSafetyWindowAreReported();
RestartKeepsPreviousSegment();
PunctuationRevisionsDoNotShiftWordStarts();
LeadingPunctuationDoesNotMoveWindowStart();
WeakAnchorStartsAndThenUpdatesNewSegment();
FormatsReadableParagraphsWithoutChangingText();
DoesNotSplitCommonAbbreviations();
ResumeMarkerIsAnIndependentFormattingBoundary();
ResumeDiscardsExcludedIntervalAndCapturesOnlyNewText();
ResumeWorksWithShortExcludedBaseline();
ResumePreservesVeryShortWantedPhrases();
EditedFrozenTextSurvivesResume();
EarlierBlocksRemainEditableAfterSeveralResumes();

Console.WriteLine("All TranscriptAssembler tests passed.");
