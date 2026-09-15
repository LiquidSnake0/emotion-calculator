namespace Emotion.Signal;

/// <summary>
/// Ce qu'un analyseur a le droit de faire, selon l'entree qu'il ecoute.
///
/// LE CUE APPREND, LE MASTER JOUE — et ce sont deux processus en parallele. Le premier fixe
/// les regles d'un disque pendant le beatmatch : quelles frequences portent quoi, le
/// boom-tchak, le tempo. Le second, une fois les regles recues, n'apprend plus rien : il
/// suit en temps reel avec tout ce qu'il faut deja en main, et ne mesure que la variance,
/// le pitch que le DJ pousse et les niveaux qu'il coupe a l'EQ. « e-e est le DJ visuel qui
/// copie ce que je fais dans la maniere de le faire. »
/// </summary>
public enum RoleAnalyseur
{
    /// <summary>Une seule entree : l'analyseur apprend et joue, comme avant.</summary>
    Solo,

    /// <summary>La sortie casque : il apprend le disque qui vient, et le transmet.</summary>
    Cue,

    /// <summary>La sortie salle : il ne forme jamais de portrait, il recoit et il suit.</summary>
    Master,
}
