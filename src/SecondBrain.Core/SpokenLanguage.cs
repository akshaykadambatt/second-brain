namespace SecondBrain.Core;

public static class SpokenLanguage
{
    public const int DefaultLevel = 2;
    public static string Name(int level) => level switch { 1 => "Plain", 2 => "Conversational", 3 => "Balanced", 4 => "Technical", 5 => "Specialist", _ => throw new ArgumentOutOfRangeException(nameof(level)) };
    public static string Instruction(int level) => $" Speaking style: jargon level {level}/5 ({Name(level)}). " + (level switch
    {
        1 => "Use everyday words and very short, direct sentences. Replace jargon with its plain meaning. If an exact technical term is essential, explain it immediately in everyday words. For example, say 'use' instead of 'leverage' and 'make it work together' instead of 'operationalize the integration'.",
        2 => "Sound like a person talking comfortably to a colleague. Use common words, natural contractions and short sentences. Avoid corporate buzzwords, inflated phrasing and stacks of abstract nouns. Use a technical term only when needed and give its plain meaning the first time.",
        3 => "Use clear professional spoken language. Include familiar domain terms when useful and briefly explain uncommon terms. Keep sentences easy to say aloud.",
        4 => "Use precise technical vocabulary appropriate to the question. Keep the spoken sentences direct; briefly define niche abbreviations or specialist terms when their meaning is not already supplied.",
        _ => "Use precise specialist terminology appropriate to an expert audience. Keep it natural to say aloud and avoid decorative jargon or complexity that adds no meaning."
    }) + " Preserve exact names, numbers, facts and uncertainty at every level. This changes wording, not evidence, scope or confidence. Do not announce the jargon level. Apply this style to new text even if the supplied opening uses a different style.";
}
