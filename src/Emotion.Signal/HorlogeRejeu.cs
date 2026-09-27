namespace Emotion.Signal;

/// <summary>
/// Le pas cadence du rejeu : plusieurs fichiers d'une meme session, lus aussi vite que la
/// chaine les avale, mais <b>ensemble</b>.
///
/// Le fondu du cue dans le master se mesure en correlant les bandes du master a celles du
/// cue sur une seconde et demie ; en direct, les deux cartes arrivent a quelques dizaines
/// de millisecondes pres et cela suffit. Rejoues chacun de leur cote sans rythme reel, le
/// Rec Out et les voies partiraient a des vitesses differentes — trois analyseurs n'ont pas
/// le meme cout —, et la correlation comparerait des instants sans rapport.
///
/// Chaque source s'inscrit, puis attend avant chaque fenetre que toutes les autres soient
/// arrivees a moins de <see cref="ToleranceMs"/> de la sienne. Une source finie ne retient
/// plus personne. Personne ne se bloque : la plus avancee attend, les autres avancent.
/// </summary>
public sealed class HorlogeRejeu
{
    /// <summary>Un peu plus d'une fenetre d'analyse (21 ms) : au direct, deux cartes son ne font pas mieux.</summary>
    public const long ToleranceMs = 30;

    private readonly long[] _t;
    private int _inscrits;

    public HorlogeRejeu(int sources)
    {
        _t = new long[sources];
    }

    /// <summary>Le rang de la source, a passer a <see cref="Attendre"/>.</summary>
    public int Inscrire()
    {
        var rang = Interlocked.Increment(ref _inscrits) - 1;
        if (rang >= _t.Length) throw new InvalidOperationException($"plus de {_t.Length} sources inscrites");
        return rang;
    }

    /// <summary>Tant que toutes les sources ne sont pas inscrites, personne n'avance : sinon la premiere partirait seule.</summary>
    private bool Complete => Volatile.Read(ref _inscrits) >= _t.Length;

    /// <summary>Publie l'instant atteint par cette source, et attend que les autres y soient presque.</summary>
    public async Task Attendre(int rang, long tMs, CancellationToken ct)
    {
        Volatile.Write(ref _t[rang], tMs);
        while (!ct.IsCancellationRequested)
        {
            if (Complete && Retard(tMs) <= ToleranceMs) return;
            await Task.Delay(1, ct);
        }
    }

    /// <summary>De combien cette source devance la plus lente des autres.</summary>
    private long Retard(long tMs)
    {
        var min = long.MaxValue;
        for (var i = 0; i < _t.Length; i++)
        {
            var v = Volatile.Read(ref _t[i]);
            if (v < min) min = v;
        }
        return tMs - min;
    }

    /// <summary>La source a fini son fichier : elle ne retient plus les autres.</summary>
    public void Terminer(int rang) => Volatile.Write(ref _t[rang], long.MaxValue);
}
