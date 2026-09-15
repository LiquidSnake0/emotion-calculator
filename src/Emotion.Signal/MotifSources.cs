namespace Emotion.Signal;

/// <summary>
/// Le motif de chaque source : ou, dans la mesure, elle monte.
///
/// L'IDEE EST DU DJ, ET ELLE CHANGE LE ROLE DU MOTEUR.
///
/// > « Pendant le beatmatch il arrive a separer le son ; quand la musique est au master,
/// >   il doit pouvoir se debrouiller par la suite : fixer le BPM, ne plus chercher a le
/// >   retoucher une fois qu'on a capte le boom-tchak, et si au boom une note de piano
/// >   puis au tchak une autre, on garde en tete cette possibilite — reperer les patterns
/// >   de repetition. »
///
/// Le cue sert a apprendre, le master sert a jouer. Sur ce repertoire tout boucle : une
/// mesure ou deux, et ca se repete. Une fois le motif d'une source tenu, on peut
/// l'annoncer depuis la grille au lieu de l'attendre — et c'est ce qui tue le retard : un
/// motif se joue en avance, la detection ne sert plus qu'a confirmer.
///
/// CE QU'ON ACCUMULE, ET POURQUOI LA MONTEE. Mesure sur trois titres, sur les seize cases
/// de la mesure : le bit de frappe donnait des motifs vides (trop peu de frappes, une case
/// de jitter) ; le niveau donnait des motifs qui suivent la nappe et non le geste. La
/// <b>montee</b> du niveau — sa difference positive d'une image a l'autre — donne des
/// motifs stables a 0,93-0,98 d'une mesure a l'autre sur Glyph Chamber, et le motif du
/// reste y colle a celui de la batterie. Sur Timeline Explorer rien n'est stable, pas
/// meme les motifs du juge exterieur : c'est la grille qui derive sur ce titre, et la
/// stabilite du motif devient un indicateur de sante de la grille.
///
/// LA STABILITE se lit entre les mesures paires et les mesures impaires de la fenetre :
/// deux moyennes independantes qui se ressemblent, c'est un motif ; qui ne se ressemblent
/// pas, c'est du hasard qu'on aurait pris pour un motif.
/// </summary>
public sealed class MotifSources
{
    public const int Cases = 16;

    /// <summary>
    /// Mesures gardees : le motif est la moyenne de leurs montees, case par case. Seize,
    /// soit une cinquantaine de secondes a 75 BPM : le verrou qui en depend doit laisser
    /// aux instruments qui entrent apres le cue le temps d'avoir leur case (la guitare de
    /// Passepartout a soixante secondes, la troisieme source de Glyph Chamber a quatre-
    /// vingts).
    /// </summary>
    public const int Mesures = 16;

    /// <summary>Au-dessus, le motif d'une source tient ; en dessous, il flotte encore.</summary>
    public const float StabiliteSure = 0.80f;

    /// <summary>Part du maximum du motif au-dessus de laquelle une case compte comme « frappe ».</summary>
    public const float SeuilCase = 0.5f;

    private readonly int _sources;
    private readonly float[][] _historique;   // sources x (Mesures x Cases), en anneau
    private readonly float[] _mesureCourante; // sources x Cases
    private readonly int[] _comptes;          // sources x Cases : images vues par case
    private readonly int[] _mesuresVues;
    private readonly int[] _derniereCase;
    private readonly float[] _precedent;
    private readonly float[] _motif;          // sources x Cases, le motif moyen
    private readonly float[] _stabilite;

    public MotifSources(int sources)
    {
        _sources = sources;
        _historique = new float[sources][];
        for (var s = 0; s < sources; s++) _historique[s] = new float[Mesures * Cases];
        _mesureCourante = new float[sources * Cases];
        _comptes = new int[sources * Cases];
        _mesuresVues = new int[sources];
        _derniereCase = new int[sources];
        _precedent = new float[sources];
        _motif = new float[sources * Cases];
        _stabilite = new float[sources];
        Array.Fill(_derniereCase, -1);
    }

    /// <summary>
    /// Une image : le niveau d'une source, et la position dans la mesure (0 a 1), ou rien
    /// si la grille ne sait pas encore.
    /// </summary>
    public void Feed(int rang, float niveau, float? phase)
    {
        if ((uint)rang >= (uint)_sources) return;
        var montee = MathF.Max(0f, niveau - _precedent[rang]);
        _precedent[rang] = niveau;
        if (phase is not { } ph) return;

        var c = Math.Clamp((int)(Math.Clamp(ph, 0f, 0.9999f) * Cases), 0, Cases - 1);
        var derniere = _derniereCase[rang];
        // La mesure se termine quand la position retombe : on range ce qu'on a accumule
        // dans l'historique, et l'on repart.
        if (derniere >= 0 && c < derniere - Cases / 2) Clore(rang);
        _derniereCase[rang] = c;

        _mesureCourante[rang * Cases + c] += montee;
        _comptes[rang * Cases + c]++;
    }

    private void Clore(int rang)
    {
        var h = _historique[rang];
        var n = _mesuresVues[rang] % Mesures;
        for (var c = 0; c < Cases; c++)
        {
            var k = _comptes[rang * Cases + c];
            h[n * Cases + c] = k > 0 ? _mesureCourante[rang * Cases + c] / k : 0f;
            _mesureCourante[rang * Cases + c] = 0f;
            _comptes[rang * Cases + c] = 0;
        }
        _mesuresVues[rang]++;
        Recalculer(rang);
    }

    private void Recalculer(int rang)
    {
        var h = _historique[rang];
        var vues = Math.Min(Mesures, _mesuresVues[rang]);
        Span<float> paires = stackalloc float[Cases];
        Span<float> impaires = stackalloc float[Cases];
        for (var c = 0; c < Cases; c++)
        {
            float somme = 0, sp = 0, si = 0;
            for (var m = 0; m < vues; m++)
            {
                var v = h[m * Cases + c];
                somme += v;
                if (m % 2 == 0) sp += v; else si += v;
            }
            _motif[rang * Cases + c] = somme / vues;
            paires[c] = sp;
            impaires[c] = si;
        }
        _stabilite[rang] = vues >= 4 ? Correlation(paires, impaires) : 0f;
    }

    private static float Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var da = a[i] - ma; var db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        return saa > 1e-12 && sbb > 1e-12 ? (float)(sab / Math.Sqrt(saa * sbb)) : 0f;
    }

    /// <summary>Le motif d'une source, seize valeurs entre 0 et 1 rapportees a son maximum.</summary>
    public void Motif(int rang, Span<float> sortie)
    {
        if ((uint)rang >= (uint)_sources || sortie.Length < Cases) return;
        var max = 0f;
        for (var c = 0; c < Cases; c++) max = MathF.Max(max, _motif[rang * Cases + c]);
        for (var c = 0; c < Cases; c++) sortie[c] = max > 1e-9f ? _motif[rang * Cases + c] / max : 0f;
    }

    /// <summary>Le morse a venir : un bit par case, celles ou la source monte franchement.</summary>
    public ushort Masque(int rang)
    {
        if ((uint)rang >= (uint)_sources || _mesuresVues[rang] < 2) return 0;
        var max = 0f;
        for (var c = 0; c < Cases; c++) max = MathF.Max(max, _motif[rang * Cases + c]);
        if (max <= 1e-9f) return 0;
        var bits = 0;
        for (var c = 0; c < Cases; c++)
            if (_motif[rang * Cases + c] >= SeuilCase * max) bits |= 1 << c;
        return (ushort)bits;
    }

    /// <summary>A quel point le motif d'une source se retrouve d'une mesure a l'autre, -1 a 1.</summary>
    public float Stabilite(int rang) => (uint)rang < (uint)_sources ? _stabilite[rang] : 0f;

    public int MesuresVues(int rang) => (uint)rang < (uint)_sources ? _mesuresVues[rang] : 0;

    /// <summary>
    /// Le morceau est-il assez su pour qu'on cesse de le retoucher ? Oui quand au moins
    /// deux sources parmi les publiees tiennent leur motif sur la fenetre entiere.
    /// </summary>
    public bool Verrouille(int publiees)
    {
        var tenues = 0;
        for (var s = 0; s < Math.Min(publiees, _sources); s++)
            if (_mesuresVues[s] >= Mesures && _stabilite[s] >= StabiliteSure) tenues++;
        return tenues >= 2;
    }

    /// <summary>
    /// LE VERROU A DEUX VITESSES : le rythme se sait bien avant les sonorites. « Apres deux
    /// ou trois boom-tchak on a deja l'info sur le BPM, et le boom-tchak ne va pas changer. »
    /// Le motif du reste tient-il depuis <paramref name="mesures"/> mesures ?
    /// </summary>
    public bool VerrouRythme(int rangReste, int mesures = MesuresRythme) =>
        (uint)rangReste < (uint)_sources && _mesuresVues[rangReste] >= mesures && _stabilite[rangReste] >= StabiliteSure;

    /// <summary>
    /// Mesures de motif tenu avant de dire que le rythme est su. Quatre : la stabilite se
    /// lit entre les mesures paires et les impaires, il en faut deux de chaque pour qu'elle
    /// veuille dire quelque chose — une dizaine de secondes a 90 BPM, contre cinquante pour
    /// le verrou des sonorites.
    /// </summary>
    public const int MesuresRythme = 4;

    /// <summary>Le motif moyen d'un rang tel quel, sa stabilite et ses mesures vues, pour l'empreinte.</summary>
    public (float[] Motif, float Stabilite, int Vues) Exporter(int rang)
    {
        var m = new float[Cases];
        if ((uint)rang < (uint)_sources) _motif.AsSpan(rang * Cases, Cases).CopyTo(m);
        return (m, Stabilite(rang), MesuresVues(rang));
    }

    /// <summary>
    /// Pose un motif appris ailleurs sur un rang : l'historique est rempli de ce motif sur
    /// autant de mesures qu'il a ete vu, pour que la stabilite et le masque se relisent
    /// exactement comme au cue.
    /// </summary>
    public void Importer(int rang, ReadOnlySpan<float> motif, float stabilite, int vues)
    {
        if ((uint)rang >= (uint)_sources) return;
        Vider(rang);
        var n = Math.Min(Mesures, Math.Max(0, vues));
        var h = _historique[rang];
        for (var m = 0; m < n; m++) motif.Slice(0, Cases).CopyTo(h.AsSpan(m * Cases, Cases));
        motif.Slice(0, Cases).CopyTo(_motif.AsSpan(rang * Cases, Cases));
        _mesuresVues[rang] = vues;
        _stabilite[rang] = stabilite;
    }

    public void Vider(int rang)
    {
        if ((uint)rang >= (uint)_sources) return;
        Array.Clear(_historique[rang]);
        Array.Clear(_mesureCourante, rang * Cases, Cases);
        Array.Clear(_comptes, rang * Cases, Cases);
        _mesuresVues[rang] = 0; _derniereCase[rang] = -1; _precedent[rang] = 0f;
        Array.Clear(_motif, rang * Cases, Cases);
        _stabilite[rang] = 0f;
    }

    /// <summary>
    /// Deplace l'etat de chaque rang selon le relais : <paramref name="ancienRang"/>[r] dit
    /// d'ou vient le nouveau rang r, ou -1 s'il part vide.
    /// </summary>
    public void Reorganiser(ReadOnlySpan<int> ancienRang)
    {
        var copie = new MotifSources(_sources);
        for (var r = 0; r < _sources; r++)
        {
            var a = r < ancienRang.Length ? ancienRang[r] : -1;
            if (a < 0 || a >= _sources) continue;
            Array.Copy(_historique[a], copie._historique[r], Mesures * Cases);
            Array.Copy(_mesureCourante, a * Cases, copie._mesureCourante, r * Cases, Cases);
            Array.Copy(_comptes, a * Cases, copie._comptes, r * Cases, Cases);
            copie._mesuresVues[r] = _mesuresVues[a]; copie._derniereCase[r] = _derniereCase[a];
            copie._precedent[r] = _precedent[a];
            Array.Copy(_motif, a * Cases, copie._motif, r * Cases, Cases);
            copie._stabilite[r] = _stabilite[a];
        }
        for (var r = 0; r < _sources; r++)
        {
            Array.Copy(copie._historique[r], _historique[r], Mesures * Cases);
            Array.Copy(copie._mesureCourante, r * Cases, _mesureCourante, r * Cases, Cases);
            Array.Copy(copie._comptes, r * Cases, _comptes, r * Cases, Cases);
            _mesuresVues[r] = copie._mesuresVues[r]; _derniereCase[r] = copie._derniereCase[r];
            _precedent[r] = copie._precedent[r];
            Array.Copy(copie._motif, r * Cases, _motif, r * Cases, Cases);
            _stabilite[r] = copie._stabilite[r];
        }
    }

    public void Reset()
    {
        foreach (var h in _historique) Array.Clear(h);
        Array.Clear(_mesureCourante);
        Array.Clear(_comptes);
        Array.Clear(_mesuresVues);
        Array.Fill(_derniereCase, -1);
        Array.Clear(_precedent);
        Array.Clear(_motif);
        Array.Clear(_stabilite);
    }
}
