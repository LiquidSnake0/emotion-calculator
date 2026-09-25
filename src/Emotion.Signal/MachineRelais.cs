namespace Emotion.Signal;

/// <summary>
/// Les trois temps du relais, et le quatrieme qui manquait.
///
/// La machine ne fait rien : elle dit a <see cref="DualAudioSource"/> QUAND agir, a partir
/// du fondu mesure. Elle vivait dans la boucle de lecture, entre deux appels d'analyseur,
/// ou personne ne pouvait la tester — et c'est la qu'un defaut a dormi : apres le retrait,
/// rien ne la remettait a zero. Le premier passage de la soiree se relayait ; tous les
/// suivants passaient sans accueil, sans tempo, sans retrait. Un set de dix disques n'avait
/// droit qu'a un relais.
///
///   accueil   le disque qui entre s'entend dans le melange (0,15) : ses portraits passent
///             au master.
///   tempo     a mi-fondu (0,5) : le master reprend le tempo appris au cue.
///   retrait   le fondu est fini (0,9) : le disque qui sortait quitte le suivi.
///   libre     le cue n'est plus dans le melange (0,1) : un autre disque est au casque,
///             tout peut recommencer.
///
/// Le tempo se relaie meme sans accueil — le cue n'avait pas encore forme de portrait —
/// et la machine s'en souvient pour se liberer quand meme.
/// </summary>
public sealed class MachineRelais
{
    public const float AccueilAt = 0.15f;
    public const float TempoAt = 0.5f;
    public const float RetraitAt = 0.9f;
    public const float LibreAt = 0.1f;

    public enum Etape { Rien, Accueil, Tempo, Retrait, Libre }

    /// <summary>0 rien, 1 accueilli, 2 tempo relaye, 3 retire — pour le journal et la sonde.</summary>
    public int Phase { get; private set; }

    private bool _tempoRelaye;

    /// <summary>Un relais est engage : le disque qui entre est deja dans le suivi du master.</summary>
    public bool EnCours => Phase is 1 or 2;

    public void Reset()
    {
        Phase = 0;
        _tempoRelaye = false;
    }

    /// <summary>
    /// Une image de plus : le fondu mesure, si le cue a un portrait a transmettre, si un
    /// tempo est disponible a relayer. Rend l'etape a executer, une par image.
    /// </summary>
    public Etape Avancer(float blend, bool cuePret, bool tempoDisponible)
    {
        if (Phase == 0 && blend >= AccueilAt && cuePret)
        {
            Phase = 1;
            return Etape.Accueil;
        }

        if (!_tempoRelaye && blend >= TempoAt && tempoDisponible)
        {
            _tempoRelaye = true;
            if (Phase == 1) Phase = 2;
            return Etape.Tempo;
        }

        if (EnCours && blend >= RetraitAt)
        {
            Phase = 3;
            return Etape.Retrait;
        }

        // LE QUATRIEME TEMPS. Retire, ou tempo relaye sans accueil : le relais a eu lieu.
        // Il ne recommence que lorsque le cue est sorti du melange, c'est-a-dire quand un
        // autre disque tourne au casque. Tant que le meme disque y reste — il joue en
        // salle et personne n'a change le disque du cue — on ne se libere pas, sinon on
        // l'accueillerait une seconde fois.
        if ((Phase == 3 || (Phase == 0 && _tempoRelaye)) && blend < LibreAt)
        {
            Reset();
            return Etape.Libre;
        }

        return Etape.Rien;
    }
}
