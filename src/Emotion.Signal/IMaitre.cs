namespace Emotion.Signal;

/// <summary>
/// Ce que le master doit savoir faire pour qu'un relais le traverse : reprendre le tempo
/// que le cue a trouve, et suivre le fondu sans former de portrait pendant qu'il dure.
///
/// <see cref="PulseAudioSource"/> au direct, <see cref="WavAudioSource"/> au rejeu d'une
/// session : les deux passent par le meme <see cref="DualAudioSource"/>, donc par le meme
/// relais. Tant que seule la source PulseAudio le savait, un set rejoue depuis ses fichiers
/// ne relayait jamais le tempo — et l'on aurait mesure un autre systeme que celui de la
/// soiree.
/// </summary>
public interface IMaitre
{
    void AdoptTempo(float bpm, long tMs);
    float Fondu { set; }
}
