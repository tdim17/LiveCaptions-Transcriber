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

static void TemporaryPeriodsAreReplaced()
{
    var assembler = new TranscriptAssembler(2);
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
    AssertEqual(
        "Italy from the first day you took office.",
        update.NewlyCommittedText,
        nameof(TemporaryPeriodsAreReplaced) + " committed text");
}

static void RollingWindowKeepsDroppedHistory()
{
    var assembler = new TranscriptAssembler(2);
    assembler.Apply("One complete. Two complete. Three current.");
    var update = assembler.Apply("Two complete. Three current. Four current.");

    AssertEqual(
        "One complete. Two complete. Three current. Four current.",
        update.FullText,
        nameof(RollingWindowKeepsDroppedHistory));
}

static void PreviousSentenceCanStillChange()
{
    var assembler = new TranscriptAssembler(2);
    assembler.Apply("One stable. Second draft. Third draft.");
    var update = assembler.Apply("One stable. Second corrected. Third draft.");

    AssertEqual(
        "One stable. Second corrected. Third draft.",
        update.FullText,
        nameof(PreviousSentenceCanStillChange));
    AssertTrue(!update.SafetyViolation, nameof(PreviousSentenceCanStillChange) + " safety");
}

static void AbbreviationDoesNotConsumeSafetySlot()
{
    var assembler = new TranscriptAssembler(2);
    var update = assembler.Apply("Mr. Smith arrived. Second sentence. Third sentence.");

    AssertEqual(
        "Mr. Smith arrived.",
        update.NewlyCommittedText,
        nameof(AbbreviationDoesNotConsumeSafetySlot));
}

static void ChangesBeyondSafetyWindowAreReported()
{
    var assembler = new TranscriptAssembler(2);
    assembler.Apply("One sentence. Two sentence. Three sentence. Four sentence.");
    var update = assembler.Apply(
        "One changed. Two sentence. Three sentence. Four sentence.");

    AssertTrue(update.SafetyViolation, nameof(ChangesBeyondSafetyWindowAreReported));
}

static void RestartKeepsPreviousSegment()
{
    var assembler = new TranscriptAssembler(2);
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

TemporaryPeriodsAreReplaced();
RollingWindowKeepsDroppedHistory();
PreviousSentenceCanStillChange();
AbbreviationDoesNotConsumeSafetySlot();
ChangesBeyondSafetyWindowAreReported();
RestartKeepsPreviousSegment();

Console.WriteLine("All TranscriptAssembler tests passed.");
