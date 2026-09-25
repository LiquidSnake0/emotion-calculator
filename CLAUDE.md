# Emotion Calculator

Le moteur d'analyse de l'ensemble **Emotion Emulator** (crate · emotion-calculator ·
emotion-renderer). Dépôt renommé d'`emotion-emulator` en `emotion-calculator` le 18 septembre
2026 ; les chemins `/dev/shm/emotion-emulator` et `~/.cache/emotion-emulator` gardent l'ancien
nom à dessein, ne pas les renommer sans en parler.

Moteur de projection temps réel pour les sets vinyle du DJ. Compagnon de
[crate](https://github.com/LiquidSnake0/crate), qui reste la base de données du bac.
Les deux se parlent par HTTP, ils ne fusionnent jamais.

Matériel visé, pas encore là : platines, table de mixage, rétroprojecteur, GPU pour le
rendu final en CUDA. **Ce qui existe aujourd'hui : les disques, le crate, un portable.**
Tout est donc construit pour être réglé sans matériel et branché sans réécriture.

## La règle qui tient tout

> **Le son donne le mouvement. La base donne le caractère.**

| Donnée | Source | Ne jamais faire |
|---|---|---|
| Attaques, énergie, bandes | le signal | — |
| Tempo, hauteurs | le signal, dérivés | **jamais verrouiller** sur le BPM d'une fiche — mais s'en servir pour savoir où chercher |
| Famille, Camelot, pochette | la base | jamais tenter de les détecter |

Un vinyle se joue à vitesse variable : ±8 % au fader, jusqu'à ±16 %. Un BPM stocké est
faux dès la première seconde.

**Mais faux n'est pas inutile, et cette nuance a coûté cher.** La règle a longtemps été lue
comme un interdit, alors que le crate est la *première* source d'information du système :
c'est de là qu'on part. La fiche ne dit pas le tempo, elle dit le **voisinage** — et
`SpectrumAnalyzer.Amorcer` recentre la préférence de l'autocorrélation dessus sans jamais
la verrouiller. Mesuré contre les tempos que le DJ a lui-même calés, sur un album entier
du bac : **43 % de justesse sans la fiche, 99 % avec**.

La largeur de la pondération suit ce qu'on sait : un quart d'octave sans fiche, **0,12
octave avec** — soit ±8,7 %, la course exacte du fader d'une platine. Plus serré ferait
mieux d'un demi-point (0,08 → 99,4 %) et trahirait le principe : à ±5,7 %, on exclurait le
disque au moment précis où le DJ pousse le pitch. Les deux morceaux entièrement faux
étaient les plus lents — 59,87 et 63,50 BPM — sous la borne d'une préférence centrée sur 90
« parce que le bac vit entre 82 et 97 ». Cet album-là va de 59,87 à 90,92. À l'inverse, détecter une tonalité pendant un fondu est
impossible — deux disques superposés produisent un accord qui n'existe dans aucun des
deux.

## Décisions à ne pas défaire sans en parler

- **Le préparé n'atteint jamais le mur.** Caler au casque ne change rien à la
  projection : sinon le public verrait le beatmatch, c'est-à-dire la coulisse.
- **On jette, on ne bloque jamais.** Files bornées en `DropOldest`, partout. Une image de
  21 ms arrivée en retard n'a aucune valeur.
- **Ne rien dire plutôt que dire faux.** `Bpm` est `float?` et reste nul tant que le vote
  n'atteint pas son tiers d'accord. Le renderer sait tourner sans.
- **Le relais amorce, il ne verrouille pas.** À mi-fondu le master reprend le tempo du
  cue comme point de départ, puis continue de chercher : le pitch a bougé pendant le
  beatmatch, et l'EQ de la table modifie le spectre.
- **Aucune œuvre dans le dépôt.** `assets/manifest.json` est versionné, les fichiers non.
- **Les enums partent par leur nom**, jamais leur rang.

## Réglages tirés du répertoire, pas de principes

Le crate vit entre 82 et 97 BPM. Plusieurs constantes en découlent et **ne sont pas
générales** — un crate de drum and bass demanderait d'autres valeurs :

| Constante | Valeur | Raison |
|---|---|---|
| `OnsetDetector.MinGap` | 20 fenêtres (426 ms) | entre la noire (620-730 ms) et la croche (310-365 ms) |
| `TempoEstimator.Preferred` | 70–110 BPM | replie l'octave sans lire de fiche |
| `SpectrumAnalyzer.KickBandLimit` | 5 bandes | le registre du kick |
| `Structure.PhraseBars` | 8 mesures | le hip-hop se construit en 8, la house en 16 |
| `TempoEstimator` repli ternaire | ×2/3 | le répertoire est joué en swing |
| `ChordChangeVote` | 0,75 | p99 de la distribution mesurée, pas une intuition |

## Une mesure qui se note elle-même ne prouve rien

Tous les indicateurs du détecteur — « intervalles sur la grille », « frappes bien calées »,
« justesse de phase » — comparent les frappes à la grille, laquelle se cale sur ces mêmes
frappes. **Un décalage commun aux deux leur est invisible par construction.**

C'est ainsi qu'un retard systématique de 21 ms a survécu à des semaines de réglages : en
interne il ne coûtait rien, et chaque tentative d'amélioration se notait contre une
référence qui portait le même biais. Il a fallu deux confrontations extérieures pour le
voir — un métronome fabriqué dont on connaissait la vérité, et les instants d'une
implémentation de référence (`aubioonset`).

Depuis : toute affirmation sur la justesse du détecteur doit s'appuyer sur au moins une de
ces deux références, jamais sur les seuls indicateurs internes.

## Deux mesures extérieures, et ce qu'elles ne disent pas

- **Le métronome fabriqué** — 90 s à un tempo exact — dit si la chaîne a un défaut
  systématique. Elle n'en a pas : 100 % d'intervalles justes, 2 ms d'erreur de phase.
- **`aubioonset`** dit si nos frappes tombent sur de vrais événements. Toujours reporter le
  niveau de hasard : avec 477 attaques sur 90 s et une fenêtre de ±20 ms, aubio couvre déjà
  20 % du temps.

**Aucune des deux ne mesure la pulsation.** La justesse dit « est-ce un vrai événement »,
jamais « est-ce le bon » — un détecteur qui tire sur toutes les attaques y excelle et rend la
grille inutilisable. C'est exactement ce qui arrive au blanchiment adaptatif : +9 points de
justesse, −7 points de verrouillage.

- **Le temps d'accroche** se mesure par la sonde, et distingue deux instants qu'il ne faut
  jamais confondre : quand le **tempo** publié se pose à moins de 1,5 BPM de sa valeur finale
  (le critère est en BPM et non en pour cent, parce que le tempo publié bouge par pas d'un
  BPM entier), et quand le **temps fort** dépasse 0,35. Chacun doit *tenir* cinq secondes.

  Repères sur treize morceaux : **tempo 15,1 s, temps fort 30,2 s**. Et un arbitrage
  structurel dont aucune combinaison essayée ne s'échappe — quatre secondes gagnées sur le
  tempo en coûtent cinq au temps fort, parce que le vote du temps fort accumule ses indices
  sur une grille qu'il suppose stable.

- **`tools/Emotion.Pulse`** est la troisième mesure, et celle qui tranche désormais. Elle
  demande si une suite d'instants forme un pouls, par la statistique de Rayleigh sur des
  fenêtres de quinze secondes. Aucune tolérance à régler, un niveau de hasard qui se calcule,
  et deux sorties : la **force** (concentration) et la **stabilité** (part des fenêtres qui
  retrouvent la même période). Elle ne consulte aucune grille.

  Repères : le métronome donne 0,973 et 100 %. Le répertoire donne **0,695 et 45 %**.

- **Elle compte trois grandeurs, et aucune ne se lit seule.** La **couverture** — marquages
  par période — a été ajoutée après coup, parce que force et stabilité se trichent sans elle :
  en durcissant un seuil, la stabilité montait de 33 à 55 % pendant que les frappes tombaient
  à une pour trois temps. Ce qui est rendu doit être juste, régulier, **et à peu près
  complet**.

**Toute comparaison de détecteur passe désormais par elle.** C'est elle qui a clos le débat
étroit/blanchi — match nul, 0,623 contre 0,645 — après que les deux autres familles
d'indicateurs eurent donné des réponses opposées et également invérifiables.

## Ce que le diagnostic a appris

Chaque correction vient d'une mesure, pas d'une intuition. À conserver dans cet esprit.

| Symptôme | Cause réelle | Correction |
|---|---|---|
| 4 attaques par temps (341 BPM) | flux sur tout le spectre, saturé par le souffle et le crépitement | flux limité au registre du kick |
| écart médian ≈ écart minimal | pas de condition de maximum local | exiger un sommet, pas un franchissement |
| `kick 80` et `clap 81` simultanés | tranches de registre qui se chevauchaient | tranches disjointes + dominance du médium |
| corrélation à 100 % fader fermé | deux morceaux se ressemblent spectralement par construction | centrer bande par bande, garder la dynamique |
| flux arrêté sans erreur | `parec` bloqué sur une sortie d'erreur jamais lue | drainer stderr, relancer si mort |
| 22 µs par message GPU | `ParseHex` et `Scene.ForFamily` sur le chemin chaud | calculer une fois dans `TrackContext` |
| pire cas à 7,5 ms | pages mappées non matérialisées | pré-toucher l'anneau à l'ouverture |
| « les éclairs c'est trop chelou » | **128 ms de retard** — deux étages réglés sans additionner leur total | une fenêtre chacun : 43 ms, et la détection s'est *améliorée* |
| tempo publié sur 4 % des fenêtres | vote sur les écarts entre attaques consécutives, par cases de 1,4 % | autocorrélation de l'enveloppe |
| intervalle de mesure à 149 ms | la grille recalculait sa position depuis une origine lointaine : changer la période faisait sauter le rang de seize temps | la phase s'accumule, elle ne se recalcule pas |
| 0,90 de confiance sur du bruit blanc | confiance mesurée sur la forme de la courbe, dont la moitié vaut zéro par troncature | sur la hauteur de la corrélation, qui est absolue |
| écart médian entre kicks = 1,21 temps | courbe moyennée sur deux fenêtres avant jugement : un pic d'une fenêtre en ressort étalé sur **deux fenêtres égales**, que le maximum local strict rejette | juger le kick sans lissage — 1,00 temps, intervalles justes 35 → 52 % |
| le battement se figeait, les sources semblaient « buguer » | le renderer consommait les impulsions **par image de rendu** et non par image d'analyse : 47 images/s côté signal, 120 côté écran, donc chaque frappe lue deux à trois fois — et `clock.sync()` rappelé sur la même frappe, ce qui épinglait la phase | une barrière `neuve` : une impulsion se consomme une fois par image d'analyse |
| une seule bavure décalait tout le train | le détecteur devient sourd 0,85 temps après avoir tiré : une bavure au quart du temps bloque le vrai kick, et la détection suivante retombe au quart du temps suivant | la **fermeté** — une frappe doit valoir 0,55 fois la médiane des huit précédentes ; stabilité du pouls 33 → 45 % |
| toutes les frappes publiées 17 à 30 ms trop tard | l'instant valait `tMs + offset_courant` alors que la frappe est jugée sur la fenêtre **précédente**, et que l'offset à employer est celui de cette précédente-là — deux erreurs de même sens | `tMs − fenêtre + offset_précédent` ; l'accord avec aubio passe de 17 à 43 % sur macro, pour un hasard de 21 % |
| `Phase` ne dépassait jamais 0,35 | remplie par `TempoTracker.Phase`, dont l'origine repart **à chaque attaque retenue** : un temps écoulé depuis le dernier coup, pas une position dans la mesure | la prendre sur `BeatGrid`, dont le « 1 » est voté et dont la phase avance seule |
| le grain sortait de sa case | `◆ ◇` avancent de 18 px et `✳ ✷` de 12,57 sur une grille réglée à 9,00 : ils ne sont pas dans la fonte monospace | palette au bon chasse, découpage sur chaque case, contrôle au démarrage |
| toute la grille battait avec le morceau | l'échelle de la scène entière était pilotée par `openness`, un descripteur de timbre calculé toutes les 21 ms et lissé par rien | seuls les gestes lents déplacent le cadre ; le filtre agit à l'intérieur des cases |
| trois extraits d'un set à 96,2 · 96,8 · 95,2 | la préférence de tempo écrasait la mesure au lieu de la départager | plancher à 0,55 : elle penche, elle ne décide pas |
| 935 changements d'accord en 2 min | seuil deviné à 0,45, soit le milieu de la distribution | 0,75, la queue — un indice fréquent ne discrimine rien |
| tension jamais au-dessus de 0,08 | diviseur des pentes à 0,5 quand le maximum réel est 0,149 | 0,12, mesuré à la sonde |
| 20 ruptures au même instant | l'arc n'avance qu'une fois par temps, son verdict était republié à chaque fenêtre | l'événement ne vaut que pour la fenêtre qui le constate |
| filtre passe-bas invisible | rien ne mesurait la *couleur* du son, seulement ses événements | centroïde et rolloff, mesurés sur le spectre entier avant HPSS |
| 17 messages mixtes sur 200 000 | course latente dans l'anneau, révélée en passant de 96 à 112 octets | vérifier le curseur **après** la copie, pas seulement avant |

## Pièges connus

- **Le mock doit remplir le même contrat qu'un vrai signal.** Il a déjà cessé de produire
  des `Hits` après un changement de contrat, et plus aucun effet ne partait en mode
  simulé. Un mock incohérent ne simule plus rien.
- **Un écran de réglage qui affiche autre chose que ce qui décide est pire qu'aucun
  écran.** Le diagnostic a montré un temps le flux global alors que les attaques venaient
  des enveloppes par registre.
- **L'écran des signaux est cadencé par l'affichage (60 Hz), le signal arrive à 47 Hz.**
  Dédupliquer sur `f.t`, sinon les attaques apparaissent doublées.
- **Ne pas mesurer un maximum brut.** Un incident unique au démarrage le fixe pour la
  soirée. Compter les dépassements.
- **Additionner les retards.** Chaque étage d'analyse en ajoute un, et 40 ms est le seuil
  où l'œil cesse de lier une image au son. `SpectrumAnalyzer.LatencyMs` doit rester
  affiché et sous ce seuil. Un filtrage payé en désynchronisation ne vaut jamais son prix
  sur un visuel.
- **La latence ne se règle pas seulement en allant plus vite.** Après avoir ramené
  l'analyse à 43 ms, il restait ~100 ms bout en bout — interpolation, affichage, dalle.
  La réponse est la prédiction : `BeatClock` verrouille une grille sur le tempo et
  déclenche le kick dessus. Écart mesuré 4,5 ms. **Mais uniquement pour le périodique** :
  prédire un clap irrégulier ou une voix inventerait des événements.
- **Dans un anneau sans verrou, constater qu'une case est valide ne suffit pas.** Entre
  la vérification et la fin de la copie, le producteur peut avoir fait un tour complet et
  réécrit la case. Il faut relire le curseur *après* et jeter la copie s'il a dépassé. Le
  défaut dormait depuis le début ; il n'est apparu que le jour où le message a grossi de
  96 à 112 octets, parce qu'une copie plus longue élargit la fenêtre. **Une course ne se
  corrige pas quand on la voit, elle se corrige quand on l'écrit.**
- **Une preuve unique ne doit pas être diluée par ce qui ne prouve rien.** La confiance
  du temps fort portait sur l'écart *relatif* au meilleur score. Or un backbeat vote
  autant pour deux hypothèses opposées : il n'apprend rien mais gonfle le total, et
  noyait la rupture de section qui, elle, tranchait — cinq centièmes de confiance pour la
  seule preuve du lot. L'écart absolu entre hypothèses *est* la somme des votes
  discriminants.
- **Certaines ambiguïtés ne sont pas des lacunes.** Kick sur 1 et 3 avec clap sur 2 et 4
  est symétrique par décalage de deux temps : l'information n'existe pas dans le signal.
  Rendre -1 est la bonne réponse ; tirer à pile ou face donnerait raison une fois sur
  deux et décalerait toute la structure l'autre fois.
- **Le mock perd le contrat à chaque fois qu'on l'étend.** Deux fois déjà — les frappes,
  puis les registres, le timbre et la structure. Un test vérifie désormais qu'aucun champ
  ne reste à sa valeur par défaut sur quatre secondes.
- **Un détecteur qui protège peut coûter plus qu'il ne rapporte.** `ContinuityWatch` a été
  commité sur la foi de ses tests unitaires, sans mesure sur du signal réel. Il voyait 5 à
  10 ruptures par 100 s sur un set où aucun disque ne saute — chacune effaçant un temps
  fort acquis en une minute d'écoute, verrouillage de 74 à 31 %. Le test ne pouvait pas le
  montrer : il supposait une détection de kick parfaite. **Un test unitaire vert ne
  remplace pas une mesure sur la matière réelle.**
- **Atténuer plutôt qu'effacer.** Effacer suppose que le détecteur ne se trompe jamais.
  En atténuant, une vraie rupture laisse la nouvelle information l'emporter en quelques
  mesures, une fausse ne coûte qu'un peu de confiance passagère.
- **Ce qui suit en continu paraît toujours calé.** L'orbe des graves n'a jamais été en
  retard parce qu'il ne décide de rien. Ne pas en conclure que le reste va bien.

## Face aux bibliothèques du domaine

Mesuré, pas supposé — 89 s d'`instamata`, morceau fiché à 87 BPM :

| Source | Tempo | Erreur |
|---|---|---|
| `aubiotrack` | 117,1 | +34,6 % |
| `aubioonset` + nos contraintes | 115,4 | +32,6 % |
| **ce projet** | **90,4** | **+3,9 %** |

**Hypothèse réfutée :** l'avantage ne vient pas des contraintes de domaine appliquées en
aval — les greffer sur `aubio` ne corrige rien. Il vient du **prétraitement** : HPSS puis
ciblage du registre du kick avant détection.

**Et ce tableau flattait.** Il porte sur un morceau isolé dont la fiche donne la vérité.
Sur un vrai set, la même méthode ne publiait un tempo que sur 4 à 11 % des fenêtres, et
quand elle en publiait un il était faux de 8 % — `aubiotrack` disait 96,4 là où nous
disions 88,9, et c'est `aubio` qui avait raison. Un bon chiffre sur un cas choisi ne dit
rien de la tenue sur la matière réelle.

En .NET il n'existe aucun équivalent d'Essentia ou d'aubio ; `NWaves` couvre le DSP mais
ni le beat tracking ni le HPSS. Le C++ reste justifié **pour le rendu GPU uniquement** —
conteneuriser l'analyse ajouterait un runtime et une frontière IPC pour un gain nul,
l'écriture d'un message coûtant 2,9 µs.

## La séparation HPSS est coupée par défaut

Elle avait été introduite pour empêcher le piano de déclencher les claps, et elle le fait —
le nombre de claps monte de 91 à 113 quand on la coupe. Mais mesurée ailleurs, elle coûte
bien plus qu'elle ne rapporte.

| Verrouillage du temps fort | Macroblank | 640 s | 900 s | 1500 s | moyenne | **pire cas** |
|---|---|---|---|---|---|---|
| avec | 20 % | **84 %** | 52 % | 35 % | 48 % | **20 %** |
| **sans** | **86 %** | 49 % | **60 %** | **43 %** | **60 %** | **43 %** |

Et la latence d'analyse tombe de **43 ms à 21**.

La raison est celle du répertoire : le barber beats étouffe et filtre ses kicks jusqu'à les
noyer, si bien que ce que la séparation retient comme percussif y est surtout du
crépitement de bande — qui n'a ni période ni accent. Elle retire du grave la basse, qui
porte la pulsation autant que la frappe.

`Signal__Separate=true` la réactive, et le restera : sur un répertoire aux kicks francs et
au piano bavard, le calcul pourrait s'inverser.

**Une voie mixte a été essayée et abandonnée** — kick avant séparation, claps après. Elle
donne 40 / 33 / 75 / 33, moins bon que les deux autres, et casse au passage la garde qui
empêche un kick de compter comme clap, laquelle compare les deux registres entre eux.

## Deux corrections trouvées par une vérité terrain

Le DJ donne un morceau de Macroblank à **87 BPM**. Le système en annonçait **108,4**.
Deux défauts empilés, dont aucun n'était visible sur le set.

### 1. La séparation détruisait la pulsation

```
                        90 BPM        108 BPM
avec HPSS               0,066         0,310      ← le mauvais tempo domine
sans HPSS               0,226 (85)    0,144
```

Le barber beats étouffe et filtre ses kicks jusqu'à les noyer. Ce que HPSS retient comme
« percussif » y est alors surtout du **crépitement de bande**, qui n'a aucune période. La
pulsation est portée par le morceau *entier* — la basse, les accords qui pulsent, tout ce
que la séparation met de côté.

> **La séparation sert à décider *ce qui frappe*, jamais *à quelle vitesse ça tourne*.**
> Le tempo se mesure donc avant elle, sur le spectre complet — exactement comme le timbre,
> et pour la même raison.

### 2. La fenêtre de Rayleigh est trop plate

Une fois la pulsation retrouvée, 85,2 BPM restait battu par 127,8 — soit **exactement son
triolet**, et le répertoire est joué en swing. La pondération classique du domaine donnait
0,80 à 128 contre 0,82 à 85 : autant dire rien.

L'oreille juge les tempos en **rapports**, pas en différences : entre 60 et 70 il y a le
même intervalle qu'entre 120 et 140. La préférence est donc gaussienne en logarithme du
tempo, à un quart d'octave d'écart-type — comme le sont déjà les bandes et le centroïde.

| Vérité terrain | Avant | Après |
|---|---|---|
| Macroblank, 87 BPM (le DJ) | 108,4 | **87,6** |
| le set, 96,1 BPM (`aubiotrack`) | 96,6 | 94,2 |

## Le tempo, par autocorrélation

L'ancienne méthode votait sur les écarts entre attaques **consécutives**. Cette seule
contrainte la condamnait : une frappe manquée double l'écart, une frappe parasite le
coupe en deux. Elle regroupait de surcroît par cases de 10 ms, soit 1,4 % d'un temps à
87 BPM — un jeu humain en sort en permanence.

`TempoTracker` autocorrèle l'enveloppe du registre du kick. Aucune décision binaire :
on mesure à quel point l'enveloppe ressemble à elle-même décalée. Trois pièces, chacune
contre un défaut précis.

| Pièce | Ce qu'elle règle |
|---|---|
| autocorrélation | supprime la dépendance aux attaques individuelles |
| fenêtre de Rayleigh | tranche l'octave sans replier à la main |
| inertie sur la courbe | donne la stabilité que le vote n'avait pas |

**Mesuré sur 100 s de set, avant puis après :**

| Extrait | Tempo publié | Valeur | Intervalle de mesure |
|---|---|---|---|
| 300 s | 4 % → **37 %** | 88,9 → **96,4** | 2688 → **2517 ms** |
| 900 s | 11 % → **94 %** | — → **96,6** | 2837 → **2496 ms** |
| 1500 s | 8 % → **96 %** | — → **95,1** | 2880 → **2496 ms** |

`aubiotrack` sur les mêmes extraits : **96,4 et 96,1 BPM**. L'ancienne méthode ne se
contentait pas de se taire, elle **se trompait** — et une référence externe était le seul
moyen de le savoir. L'intervalle de mesure tombe à 0,2 % de la valeur théorique.

Trois extraits du même set rendent presque le même tempo, ce qui avait d'abord paru
suspect : c'est au contraire attendu, un set beatmatché a par construction un seul tempo.

**Ce qui reste ouvert :** le verrouillage du temps fort varie de 38 à 74 % selon le
passage. C'est le vote du downbeat, pas le tempo.

### Le profil des quatre temps

Le vote ne portait que sur des **détections binaires** — un kick est là ou il n'est pas.
Or dans quantité de morceaux le kick tombe sur les quatre temps et n'apprend alors rien.
Mais quatre kicks présents ne sont pas quatre fois le même kick : **celui du 1 porte plus
de grave**. La présence ne discrimine pas, l'amplitude si.

`DownbeatProfile` accumule, pour chacune des quatre positions, ce qu'elle porte en
moyenne — énergie grave, montée du registre du kick, mouvement harmonique — sur des
dizaines de mesures.

| Verrouillage | 300 s | 900 s | 1500 s | pire cas |
|---|---|---|---|---|
| sans le profil | 38 % | **74 %** | 57 % | 38 % |
| **avec** | **59 %** | 71 % | 53 % | **53 %** |

Moyenne identique, **pire cas de 38 à 53**. Pour un usage live c'est le pire cas qui
compte : un passage où le système ne sait pas deux fois sur trois se voit, trois passages
moyens non.

C'est la troisième fois qu'un problème cède en passant de l'événement au continu — le
tempo, la structure longue, le temps fort.

> **Piège traversé :** j'ai d'abord jugé ce profil sur une base cassée. Branché, il
> faisait chuter le verrouillage à 21/38/31 — mais `ContinuityWatch`, commité la veille
> sans mesure, effaçait la grille 5 à 10 fois par 100 s. Une fois cette régression
> réparée, le même profil relevait le pire cas de 15 points. **Ne jamais évaluer une
> addition sur une base dont on n'a pas vérifié l'état.**

### Trois tentatives sur le vote du downbeat, trois échecs

Le score décroît de 0,99 par temps. L'idée — la sienne, et elle est théoriquement juste —
était qu'un backbeat régulier vote sans cesse pour les deux mêmes hypothèses à égalité :
il n'apprend rien, tandis que la décroissance ronge l'avance qu'un changement d'accord
avait acquise. L'oubli devrait donc être déclenché par un changement, pas par le temps.

| Tentative | Verrouillage (300 / 900 / 1500 s) |
|---|---|
| **décroissance fixe 0,99** (en place) | 38 / **74** / **58 %** |
| oubli seulement sur rupture de section | 58 / 34 / 36 % |
| renormalisation par soustraction du minimum | inchangé — le plafond n'est jamais atteint |
| oubli modulé par la dérive de la grille | 41 / 28 / 30 % |

**Ce que la mesure apprend :** un vote est exprimé dans le *référentiel de la grille*, et
ce référentiel glisse. Un vote vieux d'une minute a été porté dans une grille qui n'est
plus la même — il ne désigne plus le même temps. On n'oublie donc pas parce que la musique
change, mais parce que notre propre référence a bougé. Moduler l'oubli par la dérive
mesurée n'a pourtant pas suffi : **le mécanisme reste mal compris, et le constat empirique
tient lieu de règle en attendant.**

### Les ruptures ne marquent pas les phrases

Test direct de l'hypothèse « la musique se construit en 4, 8, 16 » : sur un compteur de
mesures **libre**, jamais réaligné, les ruptures détectées tombent-elles aux frontières ?

```
900 s   modulo 4 : 4 3 5 6    écart au hasard 1,1
1500 s  modulo 4 : 3 4 6 3    écart au hasard 1,5
```

Réparties au hasard — il faudrait dépasser 8 pour parler de structure. Le
`NoveltyDetector` signale une rupture toutes les deux mesures, ce qui est le rythme d'un
changement de timbre, pas d'une section.

**La piste est celle qui vient de marcher pour le tempo, un ordre de grandeur au-dessus :
autocorréler une signature à longue échelle** pour trouver la période de la section
(10 à 45 s) comme on trouve celle du temps (0,3 à 1 s). Une phrase se répète, donc elle
se corrèle avec elle-même. → `SectionTracker`, ci-dessous.

**Piège de mesure à ne pas refaire :** mesurer la position des ruptures sur le compteur
`Bar` est circulaire, puisque `AlignPhrase` le remet à zéro à chaque rupture. Les trouver
sur la mesure 0 ne prouve rien.

## Structure## La structure longue : ce qu'elle trouve, et ce qu'elle ne trouve pas

`SectionTracker` autocorrèle une signature de mesure — 12 bandes, brillance, densité —
sur cinq hypothèses seulement : 4, 8, 16, 32 mesures. **Deux n'y figure pas** : à cette
échelle on décrit le motif que le batteur répète *à l'intérieur* de la phrase, qui se
corrèle mieux que la vraie phrase et n'apprend rien. Mesuré en la laissant : elle
l'emportait deux fois sur trois.

**Sur signal structuré, ça marche parfaitement.** Ressemblance mesurée : **0,999** pour
une phrase de 8 régulière, **0,092** sur du bruit. Un facteur dix.

**Sur le set du DJ, la confiance est nulle.** Meilleur score 0,002. Le suivi dit qu'il
ne sait pas, ce qui est la bonne réponse — mais ça reste un résultat négatif :

> Sur le master, **deux morceaux se superposent**. La signature d'une mesure y mélange ce
> qui sort et ce qui entre, et aucune phrase ne peut se corréler avec elle-même. C'est
> l'argument du DJ pour le cue : au casque le morceau est **seul**, et c'est là que sa
> structure est lisible. La structure longue est probablement une mesure de cue, pas de
> master.

### Trois métriques de confiance, deux fausses

| Mesure | Sur du bruit | Sur une phrase franche |
|---|---|---|
| écart à la moyenne des hypothèses | **1,00** ✗ | 1,00 |
| écart au poursuivant / dispersion | 0,56 | **0,00** ✗ |
| **valeur absolue** (en place) | 0,18 | 1,00 |

La deuxième échoue pour une raison qui vaut d'être retenue : **16 mesures est le double de
8**, donc quand la réponse est 8 son harmonique se corrèle presque autant. La bonne
réponse et son double se tiennent, ce qui écrase l'écart au suivant précisément quand tout
va bien.

### Le faux positif qu'il ne faut pas refaire

Le suivi a d'abord rendu « 8 mesures, 100 % du temps », avec des phrases espacées de
**20,0 s** — soit exactement 8 mesures à 96 BPM. Tout concordait. C'était la **valeur par
défaut jamais modifiée** : une hypothèse manquant de données valait zéro, les scores réels
étant négatifs, et l'estimateur sortait sans rien décider.

> **Une valeur par défaut qui a l'air juste est plus dangereuse qu'une erreur franche.**
> Un test vérifie désormais qu'un signal sans répétition ne produit aucune confiance.

Les scores sont négatifs, et c'est normal : la somme de vecteurs centrés est nulle, donc
la somme de leurs produits scalaires vaut l'opposé de la somme de leurs carrés. On compare
les hypothèses entre elles, jamais à zéro.

## Emprunts à `bonk~`

Le détecteur d'attaques de Puckette, en production depuis 1998. Voir `docs/pd-gem.md`.

| Mesure sur 3 × 100 s | Avant | Après |
|---|---|---|
| écart médian entre kicks | 619 / 597 / 597 ms — **0,94 temps** | **619 / 619 / 619 ms — 0,97 à 0,99 temps** |
| kicks détectés | 140 / 157 / 155 | 142 / 139 / 140 |
| verrouillage du temps fort | 59 / 71 / 53 % | 54 / 76 / 51 % |
| charleys | 331 / 302 / 302 | 331 / 302 / 302 |

Les kicks tombent désormais **sur** les temps, avec un écart identique d'un passage à
l'autre. Le reste est inchangé.

**Le rapport et le masque sont indissociables.** Pris contre la fenêtre précédente — qui
peut être quasi nulle — un rapport explose sur du bruit : les charleys tombaient de 331 à
121. Il se prend contre un masque qui suit la crête, seule référence stable.

**Le masque doit s'effacer entre deux frappes.** J'ai voulu « adapter » le `maskdecay` de
0,7 à 0,94 pour compenser nos fenêtres huit fois plus longues. C'était l'inverse du
raisonnement : s'il tient encore quand la frappe suivante arrive, une frappe régulière de
même amplitude ne produit aucun rapport. **5 charleys détectés au lieu de 302.** La valeur
d'origine était la bonne.

**Un rapport n'a de sens que dans un registre qui se vide.** Les aigus de ce répertoire ne
se vident jamais — souffle de bande et crépitement de vinyle y entretiennent un plancher
permanent, ce qui avait déjà forcé à limiter le flux au registre du kick. Le grave et le
médium prennent le rapport ; **les aigus gardent la différence**.

### Emprunt 3 : non prouvé, mais il a trouvé autre chose

`TransientLocator` situe l'attaque *dans* la fenêtre — passe-bas sur le signal brut, huit
sous-blocs de 2,7 ms — pour que la grille se cale sur l'instant réel de la frappe plutôt
que sur celui de la fenêtre qui la contient.

**Aucune métrique existante ne bouge d'un chiffre.** C'est normal : le verrouillage et
l'écart entre frappes sont comptés à la fenêtre, ils ne peuvent pas voir 21 ms.

La métrique qu'il a fallu créer pour le juger — l'écart des frappes à la grille — a révélé
autre chose :

```
300 s   écart moyen 0,200 temps = 124 ms   dispersion 0,140
900 s   écart moyen 0,217 temps = 139 ms   dispersion 0,125
1500 s  écart moyen 0,225 temps = 144 ms   dispersion 0,149
```

**Une distribution parfaitement aléatoire donnerait 0,25.** La grille n'est donc presque
pas synchronisée avec les frappes qu'elle suit — et c'est cohérent avec le verrouillage du
temps fort qui plafonne à une fenêtre sur deux : *un vote porté dans une grille mal calée
désigne un temps au hasard.*

Ce défaut domine complètement les 21 ms que l'emprunt 3 corrige. Il est donc gardé mais
**non prouvé**.

### Et le diagnostic s'est retourné : ce n'est pas la grille

En cherchant à corriger la phase, deux mesures ont renversé la conclusion :

```
période de la grille  625 / 619 / 634 ms      tempo publié  625 / 619 / 628 ms   → écart 0 à 0,9 %
intervalles entre kicks tombant sur un multiple du temps :  51 % · 39 % · 46 %
frappes à moins de 0,1 temps de la grille :                 35 % · 20 % · 27 %
```

La grille tourne exactement au bon tempo. Mais **moins d'une frappe détectée sur deux
tombe sur un multiple du temps.** La détection produit autant de bruit que de signal, et
une boucle qui se corrige autant sur l'un que sur l'autre poursuit le bruit.

> **Le problème n'est pas dans `BeatGrid`, il est dans `OnsetDetector`.** J'ai passé la
> soirée à améliorer le vote du temps fort, puis la phase de la grille, alors que les deux
> reposent sur des frappes fausses une fois sur deux.

### Quatre tentatives sur la phase, quatre reculs

| Tentative | Verrouillage (300 / 900 / 1500 s) |
|---|---|
| **état commité** | **54 / 76 / 51 %** |
| `Sync` utilisant enfin l'instant réel de la frappe (correction d'un vrai bug) | 24 / 55 / 39 % |
| plus une fenêtre de capture gaussienne | 25 / 65 / 50 % |

Le paramètre `tMs` de `Sync` était bel et bien reçu et jamais lu — un vrai défaut, qui
rendait `TransientLocator` entièrement inopérant. **Le corriger dégrade** : le système
bénéficiait accidentellement du bug.

La fenêtre de capture — ne se laisser tirer que par ce qui tombe près de la grille — est
la bonne réponse de principe, et elle échoue pour une raison connue : un PLL sans phase
d'acquisition ne peut pas s'accrocher, puisque tant qu'il est mal calé, *toutes* les
vraies frappes lui paraissent lointaines. Il faudrait une fenêtre large au départ,
resserrée ensuite.

**Mais rien de tout cela ne vaut avant d'avoir moins de 50 % de fausses frappes.**

### Le détecteur d'attaques : deux échecs, et un fait rassurant

Deux tentatives, aucune ne bat l'existant :

| Tentative | kicks | sur les temps | verrouillage |
|---|---|---|---|
| **en place** (moyenne × 1,8) | 142 / 139 / 140 | 43 / 34 / 38 % | **54 / 76 / 51 %** |
| médiane × 1,8 | 167 / 102 / 108 | 34 / 39 / 43 % | 24 / 60 / 32 % |
| médiane + 3 écarts absolus médians | 136 / 95 / 104 | 45 / 37 / 39 % | 51 / 45 / 35 % |

La médiane est pourtant la statistique juste — une moyenne est tirée vers le haut par les
pics qu'elle sert à détecter. Elle échoue quand même, et **réduire le nombre de détections
n'améliore pas leur qualité** : les frappes supprimées étaient autant des bonnes que des
mauvaises.

**Comparaison externe, sur les mêmes extraits :**

| | attaques / 100 s | écart médian | sur les temps | sur les croches |
|---|---|---|---|---|
| `aubioonset -O hfc` | 483 · 617 | ~160 ms | 3 · 4 % | 16 · 3 % |
| **ce projet** | 142 · 139 | **619 ms** | **43 · 34 %** | 35 · 28 % |

Le hasard donnerait 24 % sur les temps à cette tolérance. Nous sommes donc
significativement au-dessus, et **environ dix fois meilleurs qu'`aubio`** — qui détecte
toutes les micro-attaques sans distinguer le kick.

> **Le blocage n'est plus un algorithme, c'est l'absence de vérité terrain.** On ne peut
> pas mesurer une précision et un rappel sans savoir où sont les vrais kicks. `aubio` ne
> peut pas servir de référence ici : il est plus mauvais que nous. Il faut soit une
> annotation manuelle de quelques mesures, soit un signal de test dont la grille est
> connue par construction.

### La vérité terrain, enfin — et ce qu'elle a révélé

`outils/verite_terrain.py` fabrique la grille d'un morceau de studio à partir de deux
nombres. Un morceau de studio a un tempo constant : sa grille est donc entièrement décrite
par une période et une phase.

| | Source | Pourquoi c'est extérieur |
|---|---|---|
| période | **le crate**, affinée sur l'audio à ±1 % | le DJ l'a calée à l'oreille sur les platines |
| phase | l'**énergie** repliée sur cette période | aucun détecteur n'intervient, ni le nôtre ni un autre |

**Elle se vérifie sur un signal dont la vérité est dans le fichier.** Sur `etalon-kick.wav`
— des grosses caisses seules à 87,85 BPM, dont les clics se relèvent à l'échantillon — la
chaîne rend **87,854 BPM et la phase à 1,3 ms près**. Deux étapes ont été nécessaires, et
la seconde a été imposée par la mesure : le repli grossier sur spectrogramme s'y trompait
encore de **77 ms**, parce que pour une grosse caisse le flux culmine bien après le début
de l'attaque. Le resserrement se fait donc sur une enveloppe filtrée **à phase nulle**,
dans le domaine fréquentiel — un filtre récursif déplacerait ce qu'il mesure.

**Deux fausses pistes, toutes deux instructives.** Prendre la phase des battements
d'`aubiotrack` échouait : au double du tempo, ses instants repliés forment *deux paquets
opposés* dont la somme vectorielle est nulle, et le R de Rayleigh valait 0,005 — le hasard
exactement — sur neuf morceaux sur dix. Et supposer le tempo rigoureusement égal à la
fiche échouait aussi : deux dixièmes de pour cent d'écart font 180 ms de dérive en
quatre-vingt-dix secondes, soit un quart de temps.

### La mesure qui tranche : la concentration à la période du crate

`outils/concentration.py`. Elle replie les frappes sur la période **imposée**, et rend le R
de Rayleigh avec son niveau de hasard calculé. **Elle ne demande aucune phase** — ce qui la
rend robuste là où le rappel ne l'est pas, dater le « vrai » temps sur du barber beats
restant ambigu à quelques dizaines de millisecondes.

> **`Emotion.Pulse` ne pouvait pas répondre à cette question.** Elle cherche elle-même la
> période qui concentre le mieux : un détecteur qui pulse régulièrement sur les
> contretemps, ou une fois sur deux, y obtient une excellente note. « Est-ce un pouls » et
> « est-ce **le** pouls » sont deux questions différentes, et seule la seconde compte pour
> la projection.

### Le plafond, et où va la distance

Mesuré sur le même flux du registre grave, dix morceaux du bac :

| | R |
|---|---|
| **oracle** — sélection parfaite, un sommet par temps | **0,675** |
| sommets locaux au-dessus de moyenne + écart-type, sans grille | 0,051 |
| le détecteur | 0,239 → **0,286** |
| hasard | 0,082 |

**Le signal porte le temps.** Le prendre au plus fort ne le trouve pas — c'est le hasard.
Toute la distance entre 0,05 et 0,68 est de la **connaissance de grille**, et le détecteur
n'en parcourt qu'un tiers.

**Cinq bandes valent mieux que trois** — 30 à 410 Hz au lieu de 30 à 144. R de 0,238 à
0,286, cinq morceaux sur dix au-delà du double du hasard puis huit, accord avec
`aubioonset` de 54,5 à 59,6 % pour un hasard de 25 %. C'est un maximum franc (b4 0,270,
b6 0,270). La valeur était déjà écrite ici et pas dans le code.

**Et ce n'est qu'un tiers du chemin.** L'écart médian entre deux kicks détectés vaut encore
**1,49 temps** : le détecteur saute un temps sur trois.

### Une piste essayée et réfutée : la phase par accumulation

L'idée : les quatre tentatives de fenêtre de capture ont échoué parce qu'une boucle à
verrouillage de phase ne peut pas s'accrocher tant qu'elle est mal calée. Un histogramme de
phase, lui, examine toutes les phases candidates à la fois — pas d'état à faire converger,
donc pas de phase d'acquisition. La période est connue du crate dès la première seconde.

**Mesurée hors ligne, elle donne 188 ms d'écart médian à la vérité pour un hasard de
251 ms.** Presque rien. La mémoire n'y change rien, même infinie. La raison est une mesure
en soi :

| bande repliée | écart médian à la vérité |
|---|---|
| grave 0–160 Hz | 231 ms |
| 0–400 Hz | 114 ms |
| **médium 160–2000 Hz** | **60 ms** |
| tout le spectre | 104 ms |

**Le registre du kick est le pire endroit où chercher la phase du temps.** C'est le médium
qui la porte. « Quelle bande dit qu'il y a une attaque » et « quelle bande dit où est le
temps » ne sont pas la même question — et le projet n'avait posé que la première.

### Et il porte aussi la période, sur les morceaux feutrés

L'estimateur de tempo ne voyait que 30–410 Hz, à travers le **rapport au masque** — la même
combinaison qui avait rendu le repli de phase inutile. Sur un répertoire joué aux
instruments continus, où l'attaque est molle et le grave étouffé, cela se paie.

Une part du flux du médium versée dans l'enveloppe du tempo, mesurée sur l'album :

| `PoidsMedium` | 0 | **0,08** | 0,15 |
|---|---|---|---|
| tempo publié | 54 % | **79 %** | 78 % |
| écart au crate | 0,7 % | **0,6 %** | 0,7 % |

Les morceaux les plus feutrés gagnent le plus : t04 de 22 à 92 %, t09 de 24 à 88,
**« Passepartout » de 27 à 58** — exactement ceux où le grave n'a pas d'attaque franche.
Huit centièmes et non quinze à cause d'un seul morceau : t11, à 59,87 BPM, ne publiait déjà
qu'un tempo sur vingt fenêtres et se tait complètement à 0,15.

> **Le tempo n'était pas faux, il était absent** — et un tempo absent clignote à l'écran.
> C'est ce que le DJ voyait sur Passepartout.

## La grille tient sa phase du repli du médium, plus des frappes

**Le défaut le plus grave du projet, et il était invisible.** `BeatGrid` calait sa phase
sur les kicks détectés. Sa période est juste. Sa phase était **exactement au hasard** :
confrontée à la vérité terrain sur dix morceaux, **0,252 temps d'écart quand un tirage au
sort en donne 0,25**. C'est ce que le DJ voyait à l'écran.

`PhaseFold` empile l'énergie du médium dans un histogramme de phase et regarde où elle
s'accumule. **Toutes les phases candidates sont examinées à chaque instant** : il n'y a pas
d'état à faire converger, donc pas de phase d'acquisition — c'est ce qui le distingue des
quatre tentatives de fenêtre de capture, qui échouaient toutes parce qu'une boucle à
verrouillage de phase ne peut pas s'accrocher tant qu'elle est mal calée.

| | écart à la vérité, en fraction de temps |
|---|---|
| frappes seules (avant) | **0,252** — le hasard |
| repli + frappes (en place) | **0,187** |
| repli seul | 0,190 |

Les frappes n'apportent presque plus rien, et on les garde : atténuer plutôt qu'effacer.

### Trois branchements avant que ça marche, et chacun a appris quelque chose

| Ce qu'on lui donnait | Écart | Pourquoi |
|---|---|---|
| `BandRise` en mode rapport | 169 ms | le masque est fait pour détecter des **événements** : il s'efface entre deux frappes, donc un motif régulier n'y produit presque rien. Il efface exactement la périodicité qu'on cherche. |
| flux brut des douze bandes | 165 ms | agréger avant de différencier fait s'annuler, dans une même bande, une partielle qui monte contre une qui descend |
| **flux bin par bin, 144–1969 Hz** | **91 ms** | on différencie chaque bin, puis on somme les hausses. L'ordre inverse, et le seul qui garde le signal. |

*(hasard : 182 ms)*

### Il cherche sa période, et ce n'est pas un luxe

Le repli est d'une sensibilité qu'on ne devine pas :

| erreur de période | 0 % | 1 % | 2 % | 4 % |
|---|---|---|---|---|
| mémoire 48 temps | 85 ms | 112 | **175** | 183 |
| mémoire 12 temps | 103 ms | 104 | 128 | 147 |

**Deux pour cent suffisent à le ramener au hasard** — sur quarante-huit temps de mémoire,
cela fait un temps entier de dérive. Or le tempo du moteur est faux de 2 à 5 % sur
plusieurs morceaux. Sept histogrammes tournent donc en parallèle à ±1,5 %, et l'on garde
celui qui se concentre le mieux. Le coût est nul à côté de la FFT.

### Le kick lève l'ambiguïté du demi-temps

Le médium ne distingue pas le temps du contretemps : ce répertoire pose autant d'accords
entre les temps que dessus, et l'histogramme a deux sommets qui se ressemblent. Trois
morceaux se calaient donc **à un demi-temps**, et pire encore : sur `t04` la grille se
verrouillait *avec confiance* à côté, ce qui vaut moins que de ne pas se verrouiller.

Le kick, lui, tombe du bon côté — concentration 0,286 pour un hasard de 0,09. On lui
demande donc de départager le sommet et son antipode, **et rien d'autre** : il ne place pas
la grille, il choisit entre deux positions que le médium a déjà trouvées.

| écart à la vérité, en fraction de temps | moyenne | bien calés |
|---|---|---|
| frappes seules | 0,252 | 2/10 |
| repli seul | 0,187 | 2/10 |
| **repli + kick** | **0,178** | **4/10** |

**Le seuil de renversement n'est pas déterminé par la mesure**, et il faut le dire : 1,5
donne 0,207, 2,0 donne 0,178, 3,0 redonne 0,207, 5,0 donne 0,184. Le paysage n'est pas
monotone. Ce qui tient, c'est la présence du mécanisme, pas la valeur.

**Et c'est le pire cas qui le justifie.** Verrouillage du temps fort sur dix morceaux :

| | moyenne | pire cas |
|---|---|---|
| sans repli | 45,2 % | 14 % |
| repli seul | 62,6 % | **4 %** |
| repli + kick | 59,6 % | **17 %** |

Le repli seul gagnait trois points de moyenne de plus et *effondrait* le pire cas. Pour un
usage live c'est le pire cas qui compte — un passage où le système ne sait pas se voit,
trois passages moyens non.

### La vérité terrain a une limite, et elle a failli faire condamner le moteur

`t04` semblait le pire morceau : 333 ms d'écart. En cherchant pourquoi, on a trouvé que
**les frappes du moteur y sont fortement concentrées — R 0,54 — et tombent à 0,48 temps de
la vérité.** Un train serré ne se décale pas d'un demi-temps par accident : c'est la vérité
qui se pose au mauvais endroit, pas le moteur. Idem `t06` (−0,45). Partout ailleurs l'écart
est sous 0,08.

La cause est la même que celle qu'on venait de corriger dans le moteur : le repli
d'énergie a deux sommets qui se ressemblent, et la basse de ce répertoire tombe souvent
*après* le kick.

**Une correction a été essayée et retirée.** Départager les deux sommets par l'énergie
d'attaque du grave en réparait deux et en cassait trois. Continuer à régler ce départage
jusqu'à ce qu'il donne raison au moteur aurait été exactement la circularité que cet outil
existe pour rompre.

`noter_detecteur` rend donc **deux chiffres** : l'écart brut, qui inclut l'ambiguïté, et
l'écart replié sur un demi-temps, qui mesure la précision sans elle.

| | brut (hasard 0,25) | replié (hasard 0,125) |
|---|---|---|
| frappes seules | 0,252 | 0,119 |
| repli | 0,187 | 0,094 |
| repli + kick | **0,178** | **0,088** |

Le gain tient dans les deux colonnes, donc il ne vient pas de l'ambiguïté.

> **Lever le demi-temps demande une oreille.** Celle du DJ : il sait où est le « 1 ». Deux
> ou trois morceaux tapés à la main donneraient la seule vérité qui tranche vraiment.

### Ce qui reste

Le repli ne dit pas **quel** temps est le « 1 » — c'est une autre question, et
`DownbeatProfile` la traite. Et le plafond mesuré reste loin : une sélection parfaite sur le
même flux atteint 0,675 de concentration là où le détecteur fait 0,286.

**Deux erreurs de mesure commises et corrigées en chemin**, toutes deux dans le sens
flatteur : une tolérance relative à l'unité testée, qui rendait les croches *moins*
souvent justes que les temps — impossible, tout multiple du temps étant multiple de la
croche ; puis une tolérance de ±0,12 temps sur une grille de 0,25, qui couvre 96 % de
l'espace et annonçait fièrement « 97 % sur la grille ». **Une métrique se vérifie comme un
algorithme.**

## Structure

| Projet | Rôle | Dépendances |
|---|---|---|
| `Emotion.Signal` | modèle, analyse, sources, transport | **aucune** |
| `Emotion.Server` | endpoints du crate, boucle d'analyse | ASP.NET Core |
| `Emotion.Signal.Tests` | 170 tests | xUnit |
| `Emotion.Probe` | sonde hors ligne : un WAV entre, des chiffres sortent | — |
| `Emotion.Pulse` | la mesure de pulsation, par Rayleigh | — |
| `outils/` | le GPU simulé et l'oreille (`fenetre.py`), les mesures hors ligne | Python, PySide6 |

Le cœur se teste sans serveur, sans carte son et sans navigateur. **Le garder ainsi.**

## Faire tourner

```sh
./outils/voir.sh              # une piste au hasard de l'album, jouee, ecoutee, montree
./outils/voir.sh 5            # la piste 5   ·   ./outils/voir.sh passepartout   par le titre
./outils/voir.sh --direct     # rien ne se joue, le moteur ecoute ce que tu joues toi
```

```sh
dotnet run --project src/Emotion.Server                      # signal fabriqué
dotnet run --project src/Emotion.Server -- --sans-reseau     # aucun port ouvert
Signal__Source=pulse Signal__Device=…monitor dotnet run …    # écoute réelle
Signal__CueDevice=alsa_input.…                               # seconde entrée
Signal__Separate=false                                       # couper HPSS, pour comparer
```

```sh
dotnet run -c Release --project tools/Emotion.Probe -- <set.wav> [début_s] [durée_s]
```

Regarder l'écran renseigne sur ce qu'on voit, jamais sur ce qui décide. La sonde a
corrigé quatre constantes devinées dès sa première exécution.

`/ready` donne l'état de préparation du disque en cours, `/health` l'état des tuyaux,
`/deck/*` les commandes du crate. **C'est tout ce que le port sert désormais** : plus
aucune page, plus aucun hub, et `--sans-reseau` n'en ouvre même pas un.

Dans la fenêtre : `Q` ferme, `+` et `-` règlent l'avance du visuel. Le réglage appartient
à ce qui affiche, et à lui seul — c'est la seule pièce de la chaîne qui puisse l'appliquer.

Le port vient de `launchSettings.json` et vaut **5099** — pas 5299, qui a traîné ici et
m'a fait diagnostiquer à côté une collision de ports.

**Mesurer en `Release`.** En `Debug`, l'écriture vers l'anneau coûte trois fois plus, et
toute conclusion sur la latence serait fausse.

## Le vocabulaire visuel

**La scène est une matrice de cases, et elle n'est pas symétrique.** Empiler les formes sur
un axe unique les faisait se recouvrir quoi qu'on fasse — trois tentatives de bornage n'y
ont rien changé, parce que le problème n'était pas le calcul mais la composition : tout
partageait la même colonne.

```
┌──────────┬────────────────┬──────────┐
│ charleys │                │  aiguës  │
├──────────┤     BASSE      ├──────────┤
│  piano   │                │   VOIX   │
├──────────┴────────────────┴──────────┤
│              kick · claps            │
└──────────────────────────────────────┘
```

Une case ne peut pas mordre sur une autre puisqu'elles ne se touchent que par leurs bords,
et l'asymétrie donne à l'œil de quoi se repérer : on apprend « la voix est à droite » plus
vite que « la voix est à trente-quatre centièmes de hauteur ».

**Chaque case expose sa source à sa façon**, et ce n'est pas de la décoration : six motifs
qui se ressemblent obligent à lire l'étiquette pour savoir ce qu'on regarde, et l'œil perd
alors le temps qu'un visuel est censé lui faire gagner.

| Forme | Ce qu'elle fait | Sa composition |
|---|---|---|
| `barres` | monte | des colonnes **espacées**, depuis le bas |
| `onde` | ondule | **une** sinusoïde lente, de bord à bord |
| `masse` | enfle et retombe | pleine, centrée, **à arêtes droites** |
| `chute` | tombe | le seul mouvement **vertical** |
| `etoile` | éclate sur l'attaque | centré, **minuscule** |
| `grain` | scintille | **réparti** partout |
| `vague` | déferle | des crêtes serrées, front **continu** depuis le bas |
| *(GRAVE)* | respire | l'**anneau**, et il reste unique |

Ordre par défaut, du grave à l'aigu : barres, onde, masse, chute, **vague**, grain.

**Deux formes ont encore cédé, et c'est le DJ qui a regardé.** L'orbe enflait et retombait
comme demandé, mais ronde elle se confondait avec l'anneau de GRAVE — « la source 3 et la 7,
c'est des orbes, ça se ressemble, c'est moche ». L'anneau est la seule forme jamais dite
bonne : c'est donc à l'autre de céder. **Le geste ne change pas, la géométrie si.** Et la
comète « ressemblait à rien » : sur neuf lignes et cinquante colonnes, un déplacement
horizontal se confond avec l'onde qui traverse. Il manquait un mouvement que rien d'autre ne
fait — la verticale.

**CE QUI DISTINGUE DEUX MOTIFS N'EST PAS LEUR TRACÉ, C'EST LEUR COMPOSITION.** Anneau,
losange et étoile étaient trois dessins différents — et tous centrés, tous en contour, tous
de la même taille : trois taches identiques à un mètre. « Ils ressemblent à des anneaux
lumineux qui clignotent, et ça n'aide pas. » Ce qui les sépare désormais est *où* la matière
se trouve dans la case et *comment elle bouge* : par le bas, de bord à bord, au centre, en
déplacement, partout.

**Aucun anneau, et plus aucun rond, parmi les sources.** La case GRAVE en porte un, et c'est
la seule forme que le DJ ait dite bonne — la garder unique est ce qui la rend lisible.

**Six sources, et ce n'est pas arbitraire.** La stabilité des profils vaut 0,87–0,99 à six,
0,85–0,92 à neuf, 0,76–0,92 à douze : au-delà, la factorisation n'a plus d'objets à trouver
et découpe des instruments en morceaux qui ne se retrouvent pas d'un apprentissage à l'autre.
Le coût, lui, double — 167 ms contre 355. Le paquet réserve **huit** emplacements pour six.

**Et aucune information deux fois.** La case GRAIN du bas montrait un semis nourri des
registres aigus, c'est-à-dire exactement ce que la source 6 montre déjà, avec le même
dessin. Elle porte maintenant le **timbre** — brillance, ouverture du filtre, densité —
qui n'était montré nulle part alors qu'il décide de ce qu'on voit.

**TOUTE FORME RONDE SE MESURE EN PIXELS, ET C'EST LA CORRECTION QUI LES A DÉSEMBROUILLÉES.**
Une case fait trois fois et demie sa hauteur en largeur ; un cercle calculé en cellules y
devient une bande horizontale. C'est pour cela que l'anneau, le losange et l'étoile se
ressemblaient tous — trois motifs différents, écrasés en la même barre. La case GRAVE, elle,
mesurait déjà en pixels, et c'est la seule forme que le DJ ait dite bonne : on a généralisé
ce qui marchait.

**Les lèvres ont été retirées.** Elles voulaient dire « ce qui chante » et ne disaient rien
— « les lèvres, ça ressemble à rien ». Une masse qui enfle et retombe se lit sans
apprentissage. L'octet 3 porte donc l'orbe.

**L'aigu reçoit la vague.** Il n'a ni attaque nette ni hauteur stable, seulement une
agitation : un motif centré lui va mal, il lui faut quelque chose qui bouge partout à la
fois.

## L'enveloppe de chaque source

**Ce que le système ne savait pas dire.** Chaque source publiait son niveau, sa hauteur et
un drapeau de frappe. Rien de tout cela ne distingue une corde pincée d'un souffle : l'une
monte d'un coup puis meurt, l'autre s'installe et ne frappe jamais. Les deux produisent le
même niveau moyen et la même hauteur, et le rendu leur donnait donc le même mouvement.

> « Un instrument à corde c'est une frappe suivie d'une onde courte ou longue, un
> instrument à vent c'est en continu, il ne frappe pas. »

| | Ce que ça mesure | Aucun réglage |
|---|---|---|
| `pique` | la pente rapportée à la crête | une montée qui atteint la crête en une fenêtre vaut 1 |
| `tenue` | la moyenne rapportée à la crête | l'inverse du facteur de crête, un rapport pur |

**Les deux sont indépendantes, et c'est ce qui les rend utiles.** Un orgue : piqué faible,
tenue forte. Un woodblock : l'inverse. Un piano : piqué fort et tenue moyenne — c'est-à-dire
exactement « une frappe suivie d'une onde », où la longueur de l'onde **est** la tenue.

Deux octets par source, à l'offset 216 : le mot d'une source fait huit octets et ils sont
tous pris. Elles vivent donc dans un second bloc, ce qui ne change rien à la garantie qui
comptait — deux sources n'écrivent jamais dans le même octet.

### L'interpolation passe avant l'enveloppe, et l'ordre n'est pas indifférent

Les deux se ressemblent — toutes deux lissent quelque chose — et les intervertir détruirait
précisément ce qu'on vient de mesurer. La règle qui les sépare est celle qui tient déjà tout
le reste : **un descripteur se mêle, un événement jamais.**

- `pique` et `tenue` décrivent la **nature** d'une source. Ils se moyennent sur une seconde
  et demie en amont et ne bougent pas d'une fenêtre à l'autre : les mêler entre deux images
  ne perd rien et supprime les paliers de 21 ms. Ils passent donc par l'interpolation, avec
  le niveau.
- La **frappe** ne passe pas, et c'est pour ça qu'elle survit.

**Et l'enveloppe agit après, au rendu** : `pique` et `tenue` interpolés règlent la façon
dont l'impulsion brute retombe. Inverser — enveloppe d'abord, interpolation ensuite —
mêlerait deux retombées calculées à des instants différents, ce qui arrondirait l'attaque
même quand `pique` vaut 1. On aurait dépensé un descripteur pour décrire une netteté que le
rendu venait d'effacer.

### Ce que ça change à l'écran

| | Avant | Après |
|---|---|---|
| retombée de l'impulsion | la même pour toutes | `0,7 + 8 × (1 − tenue)` par seconde |
| force de l'attaque | `+ 0,6` pour toutes | `+ 0,15 + 0,75 × pique` |
| vitesse de l'onde | fixe | `3,4 − 2,2 × tenue` — ce qui tient ondule lentement |
| étiquette de la case | le nom de la forme | **`pincé` · `frappé` · `tenu`** |

L'étiquette existe parce qu'un écran qui montre autre chose que ce qui décide est pire
qu'aucun écran : sans elle on voit un mouvement sans savoir s'il décrit un instrument qui
frappe ou un qui souffle.

## Le « buffering » n'en était pas un

Le DJ décrit un hoquet. Mesuré, **rien ne hoquette dans le flux** : les paquets arrivent à
46,9 par seconde, zéro perdu, pire intervalle 36,9 ms ; la fenêtre rend une image en 4,5 ms
médians sur un budget de 16,7. Deux affichages sautaient, et c'est tout.

**Le tempo clignotait** parce qu'il n'était publié que quatre images sur cinq — « ne rien
dire plutôt que dire faux » porte sur ce que le moteur *affirme*, mais un nombre qui
disparaît vingt fois par seconde ne se lit pas. La fenêtre garde donc la dernière valeur
connue, **en gris** : ce n'est pas dire faux, c'est dire « voilà ce que c'était », et la
couleur dit que ce n'est plus frais. (Depuis `PoidsMedium`, il n'y a plus de trou du tout
sur macro : 0 % d'images sans tempo.)

**Le curseur de mesure reculait**, et c'était le plus visible. `Phase` vaut zéro tant que le
« 1 » n'est pas identifié — un tiers du temps — donc le curseur retombait au début du
bandeau à chaque fois que le vote lâchait. Le rattraper par la phase du temps ne faisait que
déplacer le saut : à chaque bascule entre les deux régimes, **quarante fois par minute**.

> **Un repère qui saute est pire qu'un repère absent** : l'œil suit le saut et perd la
> musique.

La fenêtre tient donc une position locale qui avance **toujours** à la cadence du temps, et
qu'elle *tire* vers la phase publiée quand celle-ci existe — la règle de partout ailleurs :
corriger une fraction, jamais recaler d'un coup. Plus aucun retour à zéro ; le pire
déplacement en une image vaut 3 % d'une mesure, quand le moteur change d'avis sur le temps
fort.

**Et il se calcule après l'interpolation**, pour la même raison que l'enveloppe : nourri du
paquet brut il avancerait par paliers de 21 ms au lieu de suivre l'écran — c'est-à-dire
qu'il produirait le hoquet qu'il est censé supprimer.

## « Absente » ne peut pas se décider sur les activations de la séparation

Le DJ observe qu'« à un moment, quelque chose qui se désactive a de la peine à se
rallumer ». Le diagnostic a trouvé une faute de principe, et trois corrections n'ont rien
changé au chiffre.

**La faute de principe, elle, est réelle.** `act[i]` était l'activation divisée par le
**maximum de l'instant** parmi les six : les sources se battaient image par image, et une
source discrète ne pouvait pas exister à côté d'une source dominante. C'est l'inverse de ce
qu'il faut — « chaque source agit sur elle-même, elle se sert des autres pour s'informer,
jamais pour se mesurer ». Elle est désormais rapportée à sa propre crête.

| ce qui a été essayé | part du temps « absente », six sources |
|---|---|
| départ | 41 – 61 % |
| seuil de silence rapporté à la source elle-même | 37 – 61 % |
| niveau rapporté à la crête propre, non au maximum des six | 26 – 72 % |
| retrait compté en **mesures** et non en secondes (5,5 s au lieu de 1,5) | 34 – 72 % |

**Aucune ne bouge le chiffre, et le neuvième décile dit pourquoi** : médiane 0,00, q90 entre
0,12 et 0,57. Les activations de la séparation sont **si piquées** qu'une source est à zéro
plus d'une image sur deux, quelle que soit l'échelle qu'on lui donne. Le problème est en
amont du seuil : « cette source joue-t-elle » n'a pas de réponse stable sur cette grandeur.

> **On a arrêté à trois.** La seule hypothèse qui reste est d'une autre nature — juger la
> présence sur une activation **lissée sur deux mesures** plutôt que sur l'instantanée,
> comme on juge déjà toutes les grandeurs continues. Elle n'a pas été essayée, et elle ne le
> sera qu'avec un critère fixé d'avance.

Les deux corrections de principe sont gardées — la concurrence entre sources était un
défaut réel, et un retrait se compte en mesures. Mais **le drapeau « absente » reste peu
fiable**, et il faut le savoir avant de fonder quoi que ce soit dessus.

## La mémoire de motif : mesurée hors ligne, et elle tient — dans UNE bande

> « Souvent on a un coup de piano qui n'est que quelques notes, genre huit notes ; ces huit
> notes une fois passées repassent après. C'est comme si on analysait une chanson avec des
> paroles : on reconnaît le refrain qui est en quatrain, dès la première écoute on a cet
> indice, puis quand on l'entend une deuxième fois on sait que c'est un refrain. »

`outils/motif.py` répond avant qu'une ligne soit écrite dans le moteur. Il découpe le
morceau en mesures, décrit chacune par **seize pas sur douze bandes**, et compare chaque
mesure à celle qui la suit de 2 à 8 mesures.

### Deux juges, parce qu'un seul se laisse tromper

| | ce qu'il demande | ce qui le trompe |
|---|---|---|
| **z** | dépasser un morceau dont on a brassé les mesures | une dérive lente : elle élève tous les décalages, et le brassage la détruit |
| **relief** | dépasser les AUTRES décalages | rien de global — seul un décalage qui ressort de ses voisins est un motif |

Les deux ne se remplacent pas : le premier dit « il y a de l'ordre », le second « cet ordre
a une période ».

### Le résultat

| | z seul | z **et** relief |
|---|---|---|
| les douze bandes ensemble | 7/10 | **1/10** |
| la meilleure bande seule | 10/10 | **8/10** |

**Le mélange ne porte pas le motif.** Ses 7/10 apparents étaient de la dérive : le morceau
change lentement, et le brassage détruit ce changement. Le second juge les efface.

**Une bande seule le porte.** Huit morceaux sur dix passent les deux juges, à des décalages
de 2, 4 ou 8 mesures — des longueurs de phrase. C'est exactement l'hypothèse du DJ : le
piano se répète même quand le reste change, et c'est pour cela qu'il faut le regarder seul.

### Ce que ça implique pour la construction

**Sur les BANDES, pas sur les sources séparées.** Les activations de la séparation ne sont
pas calées sur le temps — leur concentration est au niveau du hasard, c'est ce qui a tué le
classement des rôles. Les bandes, elles, viennent directement du spectre.

**Le « 1 » n'est pas nécessaire**, et c'est une bonne propriété : une corrélation à un
décalage ne dépend que de la *période* de la mesure, pas de son origine. Le vote du temps
fort ne verrouille que 60 % du temps ; le motif s'en passe.

**Une première version a passé son critère et ne valait rien**, et il faut le retenir. Elle
décrivait chaque mesure par la moyenne de douze bandes sur toute sa durée : 9/10 « réussis »,
avec des scores de 0,96 à 0,996 pour un hasard de 0,93 à 0,994. Moyenner sur la mesure
détruit ce qui fait un motif — sa forme **dans le temps** — et le critère était trop facile
à passer, ce qui est le défaut le plus dangereux d'une mesure : elle donne raison sans rien
prouver.

### `MotifTracker` : construit après la mesure, et confronté à elle

Le décalage trouvé par le moteur, comparé à celui que la mesure hors ligne trouve sur les
mêmes morceaux :

| | macro | t02 | t03 | t04 | t05 | t06 | t08 | t09 | t10 | t11 |
|---|---|---|---|---|---|---|---|---|---|---|
| hors ligne | 2 | 2 | 2 | 4 | 8 | 4 | — | 8 | 4 | 4 |
| moteur | 2 | 2 | **8** | 4 | 8 | 4 | — | 8 | 4 | **5** |

**Huit sur dix.** Le moteur voit moins que la mesure hors ligne — huit mesures de mémoire au
lieu du morceau entier, et une grille de mesure tirée de son propre tempo — et il tombe
pourtant sur le même chiffre huit fois.

**En revanche il ne désigne pas la même bande**, jamais. La bande qui porte un motif n'est
pas une donnée stable : plusieurs bandes le portent, et la diffusion l'étale volontairement
sur les voisines. Le décalage est le résultat ; la bande est une indication.

### Le défaut qui a coûté cinq tentatives, et sa cause

Le suivi ne voyait rien sur un signal fabriqué qui se répétait franchement. Cinq corrections
du signal de test ont échoué avant qu'un simple affichage des votes ne donne la réponse :

```
votes de la bande 5 :  2:-0.09  3:-0.11  4:0.69  5:-0.11  6:-0.10  7:-0.09  8:0.68
```

**Il voyait parfaitement.** C'est le relief qui était faux : il comptait le décalage 8 — le
**double** de 4 — parmi les rivaux. Une figure de quatre mesures se répète aussi à huit ;
c'est une conséquence de sa période, pas une période concurrente. Le relief tombait donc à
2,3 et le suivi se taisait, **d'autant plus sûrement que la réponse était franche**.

`SectionTracker` s'y était déjà fait prendre entre 8 et 16 mesures, et c'est écrit plus haut
dans ce fichier : « la bonne réponse et son double se tiennent, ce qui écrase l'écart au
suivant précisément quand tout va bien ». En écartant multiples et diviseurs du candidat, le
relief passe de 2,3 à plus de vingt.

> **Cinq tentatives sur le signal de test, une seule sur le code, et c'est le diagnostic qui
> a tranché.** Corriger le fixture pour qu'il satisfasse le code est la même faute que
> corriger le code pour qu'il satisfasse le fixture : dans les deux cas on tire des flèches.
> Afficher la grandeur intermédiaire a coûté deux minutes et donné la réponse.

## Le rôle des sources dans l'orchestre : essayé, mesuré, abandonné

L'idée était bonne et l'image du DJ juste : « le mec qui fait le tambour joue le métronome
pour les autres, celui au saxophone est chargé de jouer les mêmes notes mais à des instants
précis ». Trois rôles se mesurent en repliant une source sur la période du temps —
**métronome**, **ponctuel**, **continu** — et ils auraient dit à la grille quelles sources
peuvent témoigner à la place du kick quand il se retire.

**Elle a échoué deux fois, et la seconde ferme la question.**

| nourrie de | résultat sur six morceaux du bac |
|---|---|
| les **frappes détectées** de chaque registre | 34 « continu » sur 36 |
| les **montées de niveau** de chaque source | **36 sur 36** |

La substitution détections → énergie continue, qui a payé trois fois ailleurs dans ce
projet, ne paie pas ici. Et le diagnostic dit pourquoi :

```
tours contributifs   12 à 53        assez de matière
concentration R      0,05 à 0,17    pour un hasard de 0,13 à 0,25
```

**Ce n'est pas un problème de seuil, c'est un problème de signal.** Les montées de niveau
des six sources séparées ne sont pas calées sur le temps — leur concentration est au niveau
du hasard. La séparation produit des activations qui varient doucement et ne conservent pas
la structure rythmique. On ne peut pas mesurer la régularité d'une source à partir d'une
grandeur qui n'est pas régulière.

**Le critère d'abandon avait été fixé avant la mesure** — plus de 24 « continu » sur 36 —
et il n'y a pas eu de second essai. Les deux octets du rôle et de la place sont rendus au
paquet ; seul le **retrait** subsiste, qui ne dépend d'aucun classement.

> **Deux erreurs de méthode attrapées par les tests avant la mesure réelle**, et elles
> valent d'être notées. Le niveau de hasard était d'abord tiré de la répartition de
> l'énergie entre les cases — donc plus une source était concentrée, plus le seuil montait :
> une source parfaitement régulière rendait R = 1 pour un seuil de 1,25 et se voyait classée
> « continue ». Puis un niveau constant, qui ne monte qu'une fois en entrant, remplissait une
> case et paraissait parfaitement concentré. **Un test unitaire ne prouve pas qu'une idée
> marche, mais il coûte mille fois moins cher qu'une mesure pour prouver qu'elle est mal
> écrite.**

## Une absence n'est pas un changement

> « Le seul changement qui justifierait de recheck le beat est un changement, pas un mute
> du kick. »

Le principe est juste et il s'applique partout. Ce qui se tait n'a rien démenti : il faut
**tenir** ce qu'on savait, et ne se raviser que devant une contradiction.

| | Avant | Après |
|---|---|---|
| `BeatGrid` | la phase s'accumule seule | déjà juste |
| `GridAgreement` | fenêtre glissante de 64 frappes, sans horloge | déjà juste — par construction plus que par intention |
| verrouillage de l'horloge | un seuil sur la valeur de l'instant | **hystérésis** : on s'accroche à 0,5, on ne lâche qu'à 0,25 |
| `SourceEnvelope` | décroît pendant le silence | **gelée** après un retrait |

### Deux silences, et les confondre casse la mesure

C'est le piège de cette correction, et la première version y est tombée.

- Le silence **entre deux notes** est ce qui fait la tenue d'un pizzicato : c'est lui qui
  abaisse la moyenne sous la crête. Le geler mesurerait un pizzicato comme un souffle,
  c'est-à-dire détruirait le descripteur qu'on venait de construire.
- Le silence d'un **retrait** est autre chose : la source ne joue plus pendant des mesures.
  Celui-là doit geler, sans quoi le violon oublie qu'il était un violon pendant le creux.

**Seule la durée les sépare, et elle n'est connue qu'après coup.** On continue donc de
mesurer pendant le silence — indispensable au pizzicato — mais on garde de quoi revenir en
arrière : si le silence dure plus d'une seconde et demie, on restaure l'état de la dernière
note, sans la décroissance subie pour rien.

**Le rendu est en caractères** parce qu'il doit rester léger — il n'y a pas de GPU sous la
main, et un remplissage de texte coûte une fraction d'un dégradé. Ils donnent en prime une
identité que des polygones translucides n'avaient pas : celle d'un terminal, ce qui va bien
à un projet qui passe son temps à mesurer.

**Une voix ne se déplace pas, elle enfle.** La bande qui montait et descendait
« rebondissait comme une balle de basket ». Les lèvres qui l'ont remplacée ne se lisaient
pas davantage. Le contour mélodique déplace donc l'orbe dans **ce qui reste de place une
fois son rayon posé** — jamais librement, sinon la forme sort de sa case dès que les deux
se cumulent, et une forme coupée ne se reconnaît plus.

La palette des sources est fixe d'un disque à l'autre ; la couleur du disque teinte les
**cadres** de la matrice. Aucun rouge.

## Où vit le lissage

**Dans l'analyse, plus dans le rendu.** Chaque grandeur continue traversait un ressort
côté renderer. L'unité CUDA aurait dû les réimplémenter tous, avec les mêmes raideurs — et
une règle qui vit en deux endroits finit par vivre de deux façons, comme le Camelot avant
elle.

| | Amorti par | Raideur |
|---|---|---|
| niveau, 12 bandes | `Damper` (analyse) | 16 · 20 |
| registres grave / médium / aigu | `Damper` | 14 · 22 · 40 |
| ouverture, brillance, densité | `TimbreTracker`, déjà | — |
| tension | `ArcDetector`, déjà | — |
| **kick, clap, charley, rupture** | **personne — et c'est voulu** | — |

Les raideurs sont celles qu'employait le renderer : le mouvement ne change pas en changeant
de place, et **aucune latence n'est ajoutée** puisque le ressort existait déjà.

> **Les événements restent bruts.** Une impulsion lissée n'est plus une impulsion. C'est
> la seule chose que le renderer calcule encore — il déclenche, il n'amortit plus.

**Amortir et interpoler sont deux choses différentes, et il faut les deux.** L'amortissement
donne sa masse au mouvement ; l'interpolation comble les trous entre deux images d'analyse.
Le ressort côté rendu masquait les paliers de 21 ms *par accident* — le retirer sans
brancher `FrameLerp` sur ces grandeurs a fait apparaître une saccade que personne n'avait
introduite : elle avait toujours été là, cachée. `FrameLerp` n'était branché que sur le
niveau et les douze bandes ; il l'est désormais sur tout ce qui est continu.

Côté rendu, `Lue` remplace `Spring` partout où l'amortissement est descendu : elle porte la
valeur sans la retoucher. Garder les deux amortirait deux fois et rendrait tout mou.

## Le rendu : plus une seule ligne de JavaScript

```
crate  --HTTP REST-->  C#                       le seul réseau légitime
C#     --/dev/shm-->   outils/fenetre.py        le GPU simulé : il reçoit
C#     --/dev/shm-->   outils/fenetre_reference.py   la mesure hors ligne, sur fichier
```

Le rendu a tourné un an dans un navigateur. Il en est sorti pour une seule raison : **il
imposait un réseau là où il n'en faut aucun.** La page recevait par WebSocket ce que
l'unité de rendu lira sur PCIe ou USB-C avec un eGPU. Tant que ce maillon existait, la
latence mesurée n'était pas celle du système visé.

`outils/fenetre.py` lit exactement les 256 octets de `GpuPacket` — même contrat, même
décalages, aucune traduction. C'est ce qui en fait une mesure et non une illustration.

**Deux pièces vivent encore côté rendu, et deux seulement** (`outils/mouvement.py`), parce
qu'elles dépendent de la cadence de l'écran et non de celle du signal :

| | Ce qu'elle fait | Mesuré |
|---|---|---|
| `Horloge` | attend le temps au lieu de le constater | suit la grille à **5,2 ms** de médiane ; avance demandée 30 ms → réelle 39 |
| `Interpolation` | comble les paliers de 21 ms entre deux images | — |

### L'horloge se cale sur la grille, plus sur les frappes

Elle se calait sur les drapeaux de kick, comme le faisait le renderer web. Mesuré sur
`macro.wav` : **une frappe tous les 962 ms pour un temps de 688** — six à sept temps
marqués sur dix, et les manques ne sont pas réguliers. Une horloge ne verrouille pas sur un
train troué : la fiabilité restait à **0,00 sur tout le morceau**, et toute la stratégie de
latence du projet était inerte.

`BeatGrid` tient pourtant déjà cette phase — elle s'accumule, elle se corrige, son origine
ne bouge pas à chaque coup entendu. Elle est désormais publiée : `Structure.BeatPhase`, un
octet dans le paquet.

| | Ce que ça répond | Où c'est publié |
|---|---|---|
| `Phase` | où l'on est dans la **mesure** de quatre temps — **nulle si le « 1 » est inconnu** | offset 24, flottant |
| `BeatPhase` | où l'on est dans le **temps** — toujours remplie | offset 117, un octet |
| `GridSure` | sait-on **quel** temps est le « 1 » | offset 118 |
| `GridAgreement` | la **période** est-elle la bonne, dit par une voie indépendante | offset 119 |

**Un octet suffit** : 1/256 de temps vaut 2,7 ms à 88 BPM, huit fois plus fin que le pas de
21 ms qui la produit.

**Le verrou est `GridAgreement`, et surtout pas `GridSure`.** Les confondre a coûté une
soirée : anticiper un temps ne demande pas de savoir où l'on est dans la mesure, seulement
quand le suivant tombe. Verrouillé sur `GridSure` (médiane 0,20, jamais au-dessus de 0,45),
rien ne partait — alors que la phase publiée tourne à **87,0 temps/min pour un tempo de
87,3**. `GridAgreement`, lui, vérifie la période par les familles de frappes, formées sur le
timbre sans jamais consulter le tempo : c'est la seule vérification du projet qui ne soit
pas circulaire.

**Le seuil sort de la distribution, pas d'un chiffre rond.** Sur 70 s, l'accord se répartit
en deux modes — un bas vers 0,3 (955 images), un haut vers 0,65 (1715) — séparés par un
creux entre 0,4 et 0,5 (282). Le seuil se pose dans le creux : **0,5**.

**Ce qui est gagné, et ce qui ne l'est pas.** L'horloge suit maintenant une grille continue
au lieu d'un train troué, et comble les temps que la détection manque. Elle prédit **2 à
25 % du temps selon le passage**. Elle ne promet pas que la grille soit *alignée sur la
musique* : l'alignement ne vient que des frappes détectées, et **le défaut est dans
`OnsetDetector`, pas dans `BeatGrid`** — c'est le problème ouvert du projet.

> **La relecture de fichier n'acceptait pas la fiche.** `IAcceptsCue` et `ILearnsTracks`
> ne vivaient que sur `PulseAudioSource` : `TrackMemory.Play` ne trouvait pas d'apprenant,
> et l'amorce n'était jamais posée. **Toute mesure faite sur un WAV portait donc sur un
> moteur non amorcé** — c'est-à-dire sur l'autre système, celui à 43 % de justesse au lieu
> de 99. Et c'est précisément le chemin qu'on emprunte pour mesurer, puisqu'un fichier se
> rejoue à l'identique quand un set ne se rejoue pas. Corrigé : `WavAudioSource` implémente
> les deux.

**Il faut les deux, et les confondre coûte cher.** Amortir donne sa masse au mouvement,
interpoler comble les trous. Le ressort du renderer web masquait les paliers *par
accident* ; le retirer sans brancher l'interpolation a fait apparaître une saccade que
personne n'avait introduite — elle avait toujours été là, cachée.

Une impulsion se consomme **une fois par image d'analyse**, jamais par image de rendu :
47 images par seconde côté signal contre 60 côté écran, donc chaque frappe serait lue une
à deux fois de trop, et le recalage rappelé sur la même frappe épinglerait la phase.

**Le panneau de contrôle DJ** — listes, formulaires, état — reste un bon candidat Angular
si le DJ en veut un au dossier. Ce serait une application à part, jamais le rendu.

## La prochaine piste : étudier chaque source pour elle-même

Tout ce qui a été mesuré jusqu'ici porte sur des grandeurs **globales** — le tempo, la
phase, le motif — ou sur les six sources prises ensemble. Aucune mesure ne dit si **la
source du piano gère bien le piano**.

C'est la question suivante, et elle se pose en deux temps.

**Isoler.** Prendre un morceau dont on sait ce qu'il contient, et vérifier source par source
que ce qu'elle décrit correspond à ce qu'on entend dans son registre. Aujourd'hui on ne
sait rien de tel : on sait seulement que leurs activations sont piquées et mal calées sur le
temps — ce qui a tué le classement des rôles et rendu le drapeau « absente » peu fiable.
Ces deux échecs viennent peut-être de la même cause, et on ne l'a jamais regardée en face.

**Corréler.** Si le piano frappe tous les demi-temps et un xylophone tous les temps et demi,
les deux entretiennent un rapport qui s'analyse — et ce rapport dit quelque chose qu'aucune
source ne sait dire seule. Deux sources qui se chevauchent ne sont pas forcément une erreur
de séparation : ce peut être deux instruments qui jouent ensemble, et la différence se
mesure.

> C'est la même idée que la diffusion de chaleur sur une plaque : chaque cellule calcule
> chez elle, puis passe sa valeur aux voisines pour qu'elles s'en servent. `MotifTracker` le
> fait déjà entre bandes. Le faire entre sources demande d'abord de savoir ce que chaque
> source vaut, seule — et **la diffusion redistribue de l'information, elle n'en crée pas**.

**La mesure à faire d'abord, et le critère avant de construire :** vérifier qu'une source
isolée porte une information qu'on peut nommer. Si elle n'en porte pas — et deux mesures
laissent craindre que non — la corrélation entre sources n'aura rien à corréler.

### Un stem player, et c'est lui qui joue

> « Tu vois le stem player de Kanye West ? »

Quatre stems, un fader par stem, on monte, on coupe, on isole pendant que ça tourne. **Et le
point qu'il fallait comprendre : cet appareil ne sépare rien en temps réel.** Il *a* les
stems et ne fait que les mélanger. C'est ce qui rend la chose faisable ici — les six sources
sont extraites une fois, et la fenêtre ne fait plus que doser.

```sh
./outils/voir.sh              # une piste au hasard de l'album
./outils/voir.sh 5            # la piste 5   ·   ./outils/voir.sh passepartout   par le titre
./outils/voir.sh --direct     # rien ne se joue, le moteur ecoute ce que tu joues toi
```

| | |
|---|---|
| **clic dans une case, ou 1 à 6** | choisit la source, et elle seule s'entend — pour reconnaître ce qu'elle contient |
| **le même clic à nouveau** | tout le morceau revient, **la source reste choisie** — pour marquer dedans |
| **échap** | plus aucune source choisie |
| **bord droit d'une case** | le fader : on tire le niveau de cette piste |
| **7 et 8** | la batterie de référence, et ce que le moteur retient comme frappe |
| **espace maintenu** | un intervalle de présence, à confronter au niveau et au retrait |
| **espace tapé** | des instants, à confronter aux frappes et à la grille |
| **retour arrière** | défaire la dernière marque |
| **Q** | écrit le rapport et ferme |

**Le moteur analyse le fichier, la fenêtre joue le mélange.** Il ne peut plus écouter la
carte son : elle ne porte plus le morceau mais ce qu'on est en train de tripoter. C'est de
toute façon le seul moyen d'analyser le morceau *entier* pendant qu'on n'en écoute qu'un
sixième.

**L'attente est masquée par la musique.** Le morceau entier joue pendant que la séparation
apprend puis que l'extraction tourne — une vingtaine de secondes — et les six pistes prennent
sa place à la seconde où elles sont prêtes.

### Choisir une source et l'isoler sont deux gestes, pas un

C'est l'usage qui l'impose, et une première version l'avait manqué :

> « Si je sélectionne la source piano je veux entendre QUE le piano, et là je peux juger déjà
> visuellement. »
> « Avec la touche espace j'indique à quel rythme et quand j'entends la source **quand la
> musique entière passe**. »

Deux moments d'un même travail : on isole pour **reconnaître** ce que la source contient,
puis on remet le morceau pour marquer **où cet instrument tombe dedans**. Repérer une note de
piano dans un mélange est précisément ce que l'oreille sait faire et qu'aucune de nos mesures
ne sait faire — c'est tout l'intérêt d'avoir une oreille dans la boucle.

La première version coupait le son des cinq autres dès la sélection et rendait le second
geste impossible : **on ne peut pas taper au rythme d'un morceau qu'on n'entend plus.**

### Trois défauts trouvés en le construisant, tous par la mesure

**1. La position d'écriture n'est pas la position d'écoute.** Première version : on comptait
les échantillons poussés vers `pacat`. Mesuré, le lecteur avançait **par à-coups de 1,36 s
puis stagnait**, pour une moyenne juste. `pacat` ne respecte pas la latence qu'on lui demande :
il avale de gros paquets quand son tampon se vide. Confronté au moteur, cela donnait des
écarts de 500 à 1800 ms qui n'étaient **pas de la dérive** mais l'avance du tampon. La
position se calcule donc depuis l'horloge ; l'écriture court devant d'une avance *choisie*.

**2. Le fil de lecture écrasait les calages.** Il prend la position, calcule son bloc, écrit
la position suivante — et un calage tombé entre les deux était annulé. L'écart restait bloqué
à +513 ms, soit exactement l'intervalle entre deux calages. C'est la course de l'anneau, à
l'identique : on vérifie après coup que personne n'a bougé, et l'on jette son bloc sinon.

**3. Le retard de la chaîne audio se mesure, il ne se corrige pas en boucle.** Il vaut 10 à
60 ms selon la machine et il est **constant** : le traiter comme une dérive faisait sauter le
lecteur en permanence. On l'estime sur huit calages, on le retranche une fois, et l'écart
résiduel tombe à ±9 ms. Le premier calage d'un lecteur neuf recale sans mesurer — sans quoi
la seconde qu'il faut pour charger six pistes en mémoire est prise pour de la latence et
figée comme telle : mesuré, un « retard » annoncé à 1617 ms.

> **Et l'extraction ne peut pas vivre dans la fenêtre.** Mesuré : dix-sept secondes en ligne
> de commande, **quatre-vingt-dix-huit** dans un fil de la fenêtre. La séparation est du
> calcul Python par blocs et se disputait le verrou global avec la boucle de dessin, soixante
> fois par seconde. Dans un processus séparé : vingt-cinq secondes, et ses pannes n'emportent
> plus l'écran.

### Un juge extérieur qui NOMME les instruments — et il faut le vérifier avant de le croire

> « Je voulais que tu analyses un son de bout en bout, que tu sépares en différentes sources,
>   et que la fenêtre du temps réel se compare à ce qui a été calculé au préalable. »

C'était la demande d'origine, et elle avait été manquée : on en avait fait un travail manuel
à la touche espace alors qu'elle appelait une **comparaison automatique**.

`outils/reference.py` sépare le morceau avec **Demucs** — un modèle entraîné, d'une famille
d'algorithmes totalement différente, qui rend quatre pistes **nommées** : batterie, basse,
voix, reste. C'est exactement le rôle qu'`aubioonset` joue déjà pour les attaques : un juge
qu'on n'a pas écrit. La circularité que `LISEZMOI.md` signalait — « contrôler la séparation
demanderait de réécrire la même factorisation » — est levée.

**Ce n'est PAS une dépendance du moteur.** PyTorch vit dans un venv du cache, jamais dans le
dépôt. Un set n'est pas déterminé : un bonus track tombe sans prévenir, `SourceSeparator`
apprend ses profils en écoutant et n'a jamais besoin d'un pré-calcul. Ces fichiers ne servent
qu'à **noter** ce que le moteur trouve.

### Le juge se vérifie comme le reste, et il s'est fait prendre

Premier verdict rendu : « source 1 basse, source 6 basse » — **alors que la source 6 est la
plus aiguë du lot**. Deux minutes de diagnostic ont donné la cause :

| stem | part du son | centre de gravité |
|---|---|---|
| `bass` | **74,2 %** | 452 Hz |
| `drums` | 13,5 % | 4967 Hz |
| `other` | 9,6 % | 1180 Hz |
| `vocals` | 2,7 % | 4213 Hz |

**Sur ce répertoire, `bass` n'est pas une basse : c'est presque tout le morceau.** Corréler
nos sources à `bass` revenait donc à demander « ressemble-t-elle au morceau », à quoi la
réponse est oui pour les six.

**Le juge n'est pas cassé pour autant** : sur `etalon-kick.wav`, des grosses caisses seules,
il met **99,9 % dans `drums`**. C'est le barber beats qui le déroute — un sample de soul
ralenti et filtré ne ressemble à rien de son entraînement. *Un juge extérieur se contrôle sur
un cas dont on connaît la réponse, exactement comme un détecteur.*

**On régresse donc au lieu de corréler.** On cherche les coefficients qui reconstruisent la
source à partir des quatre pistes, et l'on regarde ce que chacune apporte **en plus des
autres**. Un stem qui contient tout n'y gagne rien — c'est la même correction que le relief
dans `MotifTracker` : un rival qui ressemble à tout le monde n'est pas un rival.

### Ce que ça dit des six sources, et ce n'est pas flatteur

Sur « Dead Internet Theory » :

| | ce que le juge y entend | part de la source expliquée |
|---|---|---|
| source 1 | basse, 55 % | **87 %** |
| source 6 | basse, 62 % | **89 %** |
| source 2 | le reste, 20 % | 24 % |
| source 3 | le reste, 17 % | 20 % |
| source 4 | le reste, 14 % | 14 % |
| source 5 | le reste, 11 % | 21 % |

Lu dans l'autre sens : **batterie → source 1 (26 %), basse → source 6 (62 %), voix →
personne (3 %)**.

**Deux sources sur six ont une identité**, et elles portent la même chose. Les quatre autres
ne sont expliquées qu'à 14-24 % : le juge ne reconnaît rien dedans. C'est le quatrième indice
concordant, après le classement des rôles, le drapeau « absente » et les rangs qui bougent.

### La frappe est isolable, elle aussi

> « Pourquoi je peux isoler les sources mais pas la frappe qui fixe les BPM ? »

Parce qu'elle n'est pas une des six : le kick ne sort pas de la factorisation par timbre, il
est détecté à part sur le registre grave — une **suite d'instants**, pas un timbre. Il n'avait
donc aucune piste audio, alors que c'est la pièce dont le tempo, la grille et la phase
dépendent toutes.

`outils/frappe.py` fabrique la piste : le morceau filtré sur le registre du kick, **coupé
partout sauf aux instants que le détecteur a retenus**. Un kick manqué s'entend comme un trou,
une frappe inventée comme un coup posé sur rien. Mesuré sur ce morceau : **320 frappes en
266 s, une toutes les 0,83 s pour un temps de 0,66** — le détecteur saute un temps sur quatre,
et maintenant ça s'entend.

Les touches **7** et **8** basculent entre la batterie réelle et ce que le moteur en retient.
C'est la mesure la plus directe qu'on ait jamais eue sur le détecteur.

### Les pistes sont taillées sur les profils de CETTE session

C'est la raison d'être de `/profils`, et elle vient d'une mesure de la veille : deux
apprentissages du même morceau trouvent les mêmes six objets — cosinus 0,77 à 0,91 — mais
**un à trois rangs sur six seulement sont conservés**. La « source 3 » d'un fichier extrait
hier n'est pas la case 3 que l'écran montre aujourd'hui.

Extraire l'album à l'avance aurait donc fait écouter une source en en jugeant une autre,
**sans que rien ne le signale**. On paie vingt-cinq secondes par morceau pour l'éviter.

### Le rapport contient les deux côtés, et c'est ce qui le rend analysable

`~/Documents/emotion-sources/rapports/<morceau>-<horodatage>.json` porte les marques **et**
ce que le moteur publiait au même instant — niveau, frappe, retrait, piqué, tenue, tempo,
phase — image d'analyse par image d'analyse, pour la seule source isolée.

> **Sans ce second côté, le journal des touches ne vaudrait rien.** Comparer ce que l'oreille
> marque à ce que la source fait suppose de connaître les deux **sur la même horloge**.
> Rejouer le morceau après coup pour retrouver les frappes du moteur donnerait un alignement
> approximatif — et c'est justement l'alignement qu'on mesure.

Environ 145 ko par minute d'annotation. Le journal ne tourne **que pendant qu'une source est
isolée** : le reste du temps il n'y a rien à confronter.

**Aucun verdict n'est affiché, et c'est voulu.** Le seul retour à l'écran est le nombre de
marques, à la place du verdict de netteté sur la case isolée. Calculer un accord en direct
obligerait à trancher tout de suite la latence de la main, qui n'est pas connue — et une
donnée corrigée par une hypothèse fausse ne se répare plus.

> **Les instants sont enregistrés bruts.** Une main tape *après* avoir entendu, de cinquante
> à cent cinquante millisecondes selon la personne et le jour. Le décalage se lit à
> l'analyse, où il reste visible : **s'il est constant, c'est la main ; s'il part dans tous
> les sens, c'est le moteur.** C'est ce qui évite d'avoir à mesurer la latence d'avance.

### La seconde fenêtre a été retirée

`fenetre_reference.py` confrontait le paquet du moteur à un rapport Python précalculé **sur
un fichier**. En écoute directe, le moteur écoute la sortie système : les deux ne parlaient
donc pas du même instant, et elle ne pouvait rien dire. Le DJ l'a vu avant nous — « elle sert
à rien, il échantillonne 90 secondes mais lesquelles ? »

Elle reste sur le disque pour les mesures hors ligne, où elle a un sens, et ne se lance plus.
Le seul morceau utile qu'elle portait — le sélecteur de faces, qui envoie la fiche au moteur
— est remplacé par l'argument de lancement : `./run.sh fichier morceau.wav 90.92`.

### Entendre ce que chaque source entend — `outils/ecouter.sh`

L'idée est du DJ, et c'est la validation la plus directe qui soit :

> « On pourrait aussi faire en sorte que tu extraies ce qu'entend chaque source. Par exemple
> `sourceXpiano.wav`, je l'écoute et je regarde si c'est vraiment le piano, si c'est en
> rythme avec le piano réel. »

```sh
./outils/ecouter.sh morceau.wav 87.06        # le BPM de la fiche, s'il est connu
```

La sonde rejoue le morceau hors ligne — **elle n'ouvre aucun port** — et exporte les six
profils spectraux appris. `outils/extraire.py` refait sa propre transformée, retrouve les
activations à profils fixés, répartit le spectre au prorata et resynthétise avec la phase
d'origine. Un WAV par source, plus un **témoin** par source.

**Le BPM de la fiche compte ici comme partout ailleurs** : sans lui le moteur cherche son
tempo dans le vide, et ce qu'il apprend des sources en dépend.

### Six contrôles passent avant que le premier fichier soit écrit

Un outil de validation qui se trompe est pire que pas d'outil : il produit une preuve à
charge contre une pièce qui n'y peut rien.

| | Ce qu'il refuse de laisser passer | Mesuré |
|---|---|---|
| reconstruction sans masque | une chaîne transformée/synthèse fausse | 146 à 149 dB |
| somme des six = le morceau | des masques qui ne se partagent pas tout | 64 à 97 dB |
| bourdonnement à la cadence | l'artefact qu'on prendrait pour de la séparation | voir ci-dessous |
| les six sont distinctes | deux fichiers qui portent le même son | **0,39 à 0,98** ✗ |
| au-delà d'un simple filtre | une factorisation qui ne ferait que couper des fréquences | 0,44 à 0,95 |
| mêmes sources deux fois | des numéros qui ne veulent rien dire | **1 à 3 rangs sur 6** ✗ |

### Le recouvrement, et le facteur cent qu'il valait

Le piège annoncé était réel, et bien pire qu'estimé. Chaque source est comparée à **son
témoin** : le morceau filtré par le même profil, mais par un gain qui ne bouge jamais — même
contenu spectral, aucune modulation possible.

| recouvrement | cadence | t02 | t04 | t05 | t09 | t11 | pire |
|---|---|---|---|---|---|---|---|
| 50 % | 93,8 Hz | 7,7 | 2,0 | **103,4** | 5,2 | 11,2 | ×103 |
| 75 % | 187,5 Hz | 2,4 | 1,7 | 7,2 | 1,7 | 1,7 | ×7 |
| **88 %** | 375,0 Hz | 4,5 | 2,4 | 2,2 | 2,2 | 1,4 | **×5** |

**Le gain décisif est entre 50 et 75 %.** Au-delà ce n'est plus tranché : 75 % gagne en
médiane, 88 % en pire cas — et c'est le pire cas qui compte, comme partout dans ce projet.
Un lissage du masque sur une longueur de fenêtre ramène la moyenne de 3,2 à 2,5 ; seize
trames au lieu de huit ne gagnent que deux dixièmes, et **on garde huit parce que c'est la
valeur que le raisonnement désigne**, pas celle qui donne le meilleur chiffre.

> **Trois juges ont été écrits pour cette seule ligne, et il faut retenir la conclusion.**
> Un fond large : piégé par les sources aiguës, qui portent peu d'énergie basse. Un fond
> local : piégé par la musique — sur t04, un pic à 360,5 Hz, plus fort dans le témoin que
> dans la source, tenait lieu de fond. Une démodulation exacte : sans moyennage, x1,0
> devenait x23,9 sur la même source. **Les trois s'accordent sur un facteur vingt et se
> contredisent sur un facteur trois.** La mesure avait la résolution de trancher le
> recouvrement, elle n'a pas celle de juger ce qui reste — l'outil rend donc trois verdicts
> au lieu de deux, dont un qui dit « je ne sais pas ».

### Et deux résultats sont tombés avant la première écoute

**Les six sources ne sont pas six objets distincts.** Sur trois morceaux sur quatre, deux
d'entre elles se ressemblent à 0,84 ou plus ; sur t04, à **0,98** — deux fichiers qui portent
le même son. Un seul morceau descend à 0,39. Sur t06 les six centres
de gravité tiennent dans une octave et demie (1011 à 2024 Hz), et les deux premiers sont
égaux au hertz près.

**Le numéro d'une source ne veut rien dire d'une lecture à l'autre.** Deux apprentissages du
même morceau, avec la même fiche, trouvent les mêmes objets — cosinus **0,77 à 0,91** sous le
meilleur appariement, dans la fourchette du 0,87–0,99 déjà mesuré — mais **un à trois rangs
sur six seulement sont conservés**. L'ordre est celui des centres de gravité ; il suffit que deux
sources voisines se croisent pour que tout glisse.

> C'est une conséquence qu'on n'avait pas vue et qui va loin : **la case 3 de l'écran ne
> montre pas le même instrument d'une lecture du disque à la suivante.** Entendre le piano
> dans `t05-source3.wav` n'apprend donc rien sur ce que la case 3 montrera ce soir. Ce qui
> est stable, c'est l'ensemble des six ; pas leurs places.

Ces deux résultats vont dans le même sens que les deux échecs déjà mesurés — le classement
des rôles, le drapeau « absente ». **L'oreille du DJ tranchera ce que les chiffres ne peuvent
pas dire** : si les six fichiers portent six choses reconnaissables, la piste vaut d'être
poursuivie ; s'ils portent tous le même morceau plus ou moins filtré, le vocabulaire à six
cases décrit une découpe en fréquences et non six instruments.

Le dossier `temoin/` est là pour ça, et c'est la comparaison qui répond : le même morceau
passé dans un simple filtre fixe. **Si `sourceN` et `temoinN` sonnent pareil, la séparation
n'a fait que couper des fréquences.** Mesuré, elles ne sonnent pas tout à fait pareil — 0,44
à 0,95 de corrélation, donc la factorisation sculpte réellement — mais c'est l'oreille qui
dit si ce qu'elle sculpte a un nom.

## Le bac de mesure : quel `tXX` est quel morceau

Les extraits de quatre-vingt-dix secondes qui servent à toutes les mesures de ce fichier
viennent de **The Era of Information** (Macroblank & slowerpace 音楽), une piste par extrait,
dans l'ordre. L'appariement a été perdu une fois et a coûté une recherche : il est écrit ici.

| | titre | fiche | | | titre | fiche |
|---|---|---|---|---|---|---|
| `t01` | NeoAtlas (Intro Theme) | 84,74 | | `t07` | Echoes of the Ancients | 77,98 |
| `t02` | Interactive WordBank | 76,97 | | `t08` | Timeline Explorer | 63,50 |
| `t03` | ThinkMap Module | 82,50 | | `t09` | Codex Sinaiticus | 86,20 |
| `t04` | Glyph Chamber | 73,50 | | `t10` | HyperText Odyssey | 77,00 |
| `t05` | Dead Internet Theory | 90,92 | | `t11` | Lost Cultures | 59,87 |
| `t06` | **Passepartout** | 87,06 | | | | |

Les fiches sont celles du crate, vérifiées une à une contre `seed.json` : onze sur onze.

**`macro.wav` n'est pas de cet album** — c'est « 07 two sided » de *RARE PSALMS COLLECTION
VOL. 4*, identifié par corrélation d'enveloppe (1,000 contre 0,304 au suivant). Ce titre
**n'est pas au crate**, donc il n'a pas de fiche : le 87,06 employé dans les anciennes
mesures est celui de Passepartout, et il a été repris par erreur. Les mesures qui s'appuient
dessus sont à relire avec cette réserve.

## Ce que l'oreille a tranché : une source prend tout, et pourquoi

> « Tout semble être dans la source 1, le reste c'est des minuscules bruits. Une dissociation
>   entre l'harmonie et les battements se fait. »

Mesuré, il a raison, et le chiffre est brutal :

| morceau | part d'énergie des six sources | rang effectif |
|---|---|---|
| Dead Internet Theory | 38,9 · 1,2 · 2,1 · 0,3 · 0,9 · **56,5 %** | **2,4 / 6** |
| Timeline Explorer | 12,9 · 25,5 · 2,4 · 3,6 · **41,7** · 13,9 % | 4,3 / 6 |

**Deux sources portent 95 % du son ; les quatre autres font 4,5 % à elles toutes.** La
factorisation ne trouve que deux objets et demi sur les six qu'on lui demande.

### Et les deux dominantes sont toutes les deux dans le grave

| | 0-150 Hz | 150-500 | 500-2k | 2k+ | crête/moyenne |
|---|---|---|---|---|---|
| **source 1** (38,9 %) | **75,0 %** | 23,4 | 1,5 | 0,1 | 7 |
| **source 6** (56,5 %) | **91,2 %** | 2,4 | 5,3 | 1,1 | 6 |
| source 2 (1,2 %) | 11,3 | 10,4 | **77,5** | 0,8 | 15 |
| source 3 (2,1 %) | 3,9 | 14,7 | **77,3** | 4,1 | 34 |
| source 4 (0,3 %) | 1,5 | 2,2 | **95,7** | 0,5 | 34 |
| source 5 (0,9 %) | 32,3 | **48,7** | 16,0 | 3,0 | 52 |

Elles ne sont pas « l'harmonie contre les battements » : **ce sont deux tranches du même
grave**, et leur facteur de crête de 6-7 dit qu'elles tiennent au lieu de frapper. Les
sources qui frappent — crête 34 à 52 — pèsent 3,5 % à elles trois.

> **La cause est mécanique et elle est ailleurs que dans l'algorithme.** La factorisation est
> pilotée par l'énergie, et 76,8 % de l'énergie de ce répertoire est sous 150 Hz. Elle passe
> donc ses composantes à découper le grave, et n'en a plus pour ce qui frappe.

Cela éclaire une confusion à ne pas refaire : « six sources, c'est le maximum » portait sur la
**netteté des profils** — se retrouvent-ils d'un apprentissage à l'autre — et non sur le
nombre d'objets réellement portés. Deux mesures, deux questions.

### Tout l'album mesuré : le rang varie, mais ce qui frappe ne pèse jamais rien

| morceau | part des six sources, du grave à l'aigu | rang |
|---|---|---|
| 01 NeoAtlas | 36,6 · 0,4 · 10,1 · 11,1 · 0,0 · 41,7 % | 3,44 |
| 02 Interactive WordBank | 2,5 · 4,8 · 0,1 · 0,8 · **87,1** · 4,7 % | 1,73 |
| 03 ThinkMap Module | **52,3** · 2,2 · 4,6 · 4,4 · 15,4 · 21,1 % | 3,73 |
| 04 Glyph Chamber | 10,6 · 24,9 · 42,9 · 1,6 · 0,6 · 19,4 % | 3,90 |
| 05 Dead Internet Theory | 38,9 · 1,2 · 2,1 · 0,3 · 0,9 · **56,5** % | 2,43 |
| 06 **Passepartout** | **89,0** · 0,8 · 3,5 · 1,0 · 1,2 · 4,4 % | **1,65** |
| 07 Echoes of the Ancients | 1,1 · 0,7 · 46,0 · 2,2 · 42,2 · 7,7 % | 2,97 |
| 08 Timeline Explorer | 12,9 · 25,5 · 2,4 · 3,6 · 41,7 · 13,9 % | 4,31 |
| 09 Codex Sinaiticus | **76,9** · 10,6 · 9,1 · 0,6 · 2,3 · 0,5 % | 2,23 |
| 11 Lost Cultures | 8,5 · 23,2 · 4,5 · 5,6 · 27,6 · 30,7 % | **4,79** |

**Le rang effectif va de 1,65 à 4,79**, médian 3,44. C'est trop variable pour être le
problème : sur *Lost Cultures* et *Timeline Explorer*, les six sources se partagent
honnêtement le son.

*Passepartout* est le pire cas — une source prend 89 % — et c'est **le même morceau** que ce
fichier désigne déjà comme le plus difficile ailleurs (« Passepartout de 27 à 58 % » de tempo
publié, le plus feutré de l'album). Deux mesures indépendantes, un seul coupable.

### Et le chiffre qui, lui, ne bouge jamais

| mesuré sur | ce qui FRAPPE (crête > 20) | ce qui TIENT (crête ≤ 10) |
|---|---|---|
| 2 morceaux | 0,6 % | 89,6 % |
| 5 morceaux | 0,5 % | 93,2 % |
| 8 morceaux | 0,5 % | 89,6 % |
| **10 morceaux** | **0,4 %** | **91,3 %** |

Et **70 % de l'énergie des six sources vit sous 150 Hz**, sur tout l'album.

> **Le système décrit des nappes avec six cases.** Les instruments qui portent le rythme —
> ceux dont le rendu a besoin pour tomber juste — occupent quatre dixièmes de pour cent de ce
> qu'il regarde. C'est vrai des dix morceaux, quel que soit leur rang.

**Le rang était donc le mauvais indicateur**, et le critère « rang > 4 » qu'on avait failli
retenir n'aurait rien réglé : *Lost Cultures* a un rang de 4,79 et souffre du même mal. Ce
qu'il faut mesurer, c'est la part de ce qui frappe.

### Égaliser le spectre avant de factoriser : mesuré, et ça échoue aussi

Le raisonnement tenait : la factorisation est pilotée par l'énergie, 70 % de l'énergie est
sous 150 Hz, donc elle y dépense ses composantes. En égalisant chaque raie par sa moyenne
longue (`Signal__Blanchiment`, de 0 à 1), le médium devrait peser autant que le grave.

**Critère fixé avant la mesure : la part de ce qui frappe doit dépasser 5 %, contre 0,4 %.**

| égalisation | rang médian | ce qui frappe |
|---|---|---|
| aucune | 2,04 | **0,4 %** |
| β = 0,25 | 1,54 | 0,5 % |
| β = 0,5 | 2,63 | **0,9 %** |
| β = 0,75 | 1,72 | 0,5 % |
| β = 1,0 | 1,27 | 0,1 % |

Le meilleur point est cinq fois sous le seuil. **Abandonnée**, sans second réglage.

> **Et elle a réfuté le diagnostic qui l'avait motivée**, ce qui vaut plus que le réglage :
>
> ```
> sans égalisation   05 : 38,9 · 1,2 · 2,1 · 0,3 · 0,9 · 56,5 %
> β = 0,5            05 :  0,7 · 5,4 · 92,7 · 0,9 · 0,2 ·  0,2 %
> β = 0,75           05 : 99,3 · 0,1 · 0,6 · 0,0 · 0,0 ·  0,0 %
> ```
>
> **La concentration ne disparaît jamais, elle change de case.** Le grave n'était donc pas la
> cause : une source prend tout quel que soit l'endroit où on met l'énergie. Le défaut est
> dans la factorisation elle-même sur cette matière, pas dans la couleur du répertoire.

Le code reste, coupé par défaut — comme HPSS, et pour la même raison : sur un autre
répertoire le calcul pourrait s'inverser. **Le masque d'extraction n'en dépend pas** : un gain
diagonal se simplifie dans le rapport `W_s·h_s / Σ W_j·h_j`, raie par raie, donc les six WAV
restent exacts quelle que soit l'égalisation.

### Le kick creuse les autres sources, et c'est structurel

> « Ils sont tous affectés par le kick, ce qui fait que le kick est rendu muet mais le son de
>   la source aussi au moment où le kick arrive. »

Mesuré au moment des 320 frappes détectées, niveau pendant rapporté au niveau juste avant :

| | source 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|
| Dead Internet | 1,46 | 1,15 | 0,97 | **0,43** | 1,66 | 0,85 |
| Passepartout | 1,29 | **0,58** | 1,25 | 1,49 | **0,51** | 1,33 |

Des sources tombent de moitié à l'instant précis où le kick frappe. **Ce n'est pas un défaut,
c'est la définition du masque doux** : les six masques somment à 1 sur chaque raie, donc une
source qui monte *prend* la part des autres. Elles ne se taisent pas, on la leur retire.

C'est le prix de la garantie « somme des six = le morceau ». Des masques indépendants —
chacun prenant ce qui lui ressemble sans contrainte de somme — supprimeraient le creusement
et perdraient l'exactitude. Le choix n'est pas tranché.

### Le fader qui claquait

Les gains passaient de zéro à un entre deux blocs. **Saut mesuré entre deux échantillons
consécutifs : 9373 unités, contre 288 avec une rampe** — soit un facteur trente-trois. Une
discontinuité s'entend comme un claquement, et c'est ce que le DJ décrivait comme « une
distorsion quand je reclique sur une source ». Le gain glisse maintenant vers sa valeur en une
soixantaine de millisecondes, comme sur n'importe quelle table.

### Séparer harmonie et percussion d'abord : mesuré, et ça échoue

La piste était la sienne et elle épousait ce qu'il entend. Elle a été mesurée avant d'être
construite, avec un juge qu'on n'a pas écrit — le stem `drums` de Demucs.

| morceau | percussif | harmonique/`drums` | percussif/`drums` |
|---|---|---|---|
| Dead Internet Theory | 40,1 % | **0,40** | 0,37 |
| Timeline Explorer | 23,2 % | **0,60** | 0,53 |

**Zéro sur deux.** Sur ce répertoire, l'harmonique de HPSS ressemble *plus* à la batterie que
son percussif. Ce n'est pas une découverte : ce fichier l'écrivait déjà — « ce que la
séparation retient comme percussif y est surtout du crépitement de bande » — et c'est
pourquoi HPSS est coupé par défaut. **Deux mesures indépendantes, à des mois d'écart,
concordent.**

> **Trois métriques ont été écrites et jetées avant celle-là**, et il faut le noter :
> facteur de crête (fixé par un seul échantillon, x1594 sur un signal vide), rapport de
> centiles (rend zéro sur des impulsions espacées), aplatissement (instable dès qu'un des
> deux côtés est petit). On tirait des flèches — alors qu'un juge déjà validé dormait dans le
> cache. **Avant d'inventer une mesure, regarder celles qu'on a.**

## Étape 2 : la mémoire à 40 s et le nombre de sources découvert

Brief du DJ, à prendre tel quel : séparation des sonorités en direct, vérifiée à l'oreille
ou contre un juge de bout en bout ; le nombre de sources n'est jamais plafonné à 6, « c'est
justement ce que le programme est censé me dire » ; **une chose à la fois**, et Passepartout
comme seul terrain. Étape 1 = mesurer combien de sources a Passepartout (coude à 4). Étape 2
= ce qui suit. Étape 3 = les faders en direct sur les sources trouvées — **pas commencée,
attend son verdict d'oreille sur l'étape 2**.

### Ce qui a changé

| | |
|---|---|
| `SourceSeparator` | `MemoireDefautS = 40f` ; ctor `(bins, sampleRate, memoire = 0)` → `Math.Max(Provisoire, 40·rate/(2·bins))`. Provisoire à 128 images et 4 sources dès le départ, puis `TryStartChoix(_v, 2, Sources, _memoire)` une fois la mémoire pleine, puis `TryStart(…, Actives, _memoire)`. `Actives`, `Bilans`, `ChoixFait`. Tous les accesseurs ordonnés sont bornés par `Actives`. `Reset()` efface tout. |
| `ProfileLearner` | K variable (`_k`, `_trames`), `Bilan(K, Reste, Doublon)`, balayage de kMin à kMax avec la **même graine** pour chaque K, arrêt si `rp - reste < 0.015` ou `doublon >= 0.90`. `Bilans` est une liste échangée atomiquement en fin de balayage — lire `/profils` en plein balayage rendait une liste à moitié écrite. |
| `SpectrumAnalyzer` | paramètre `memoireSeparationS` ; **`NewTrack()` appelle enfin `_separation.Reset()`** — les profils du disque précédent servaient de point de départ au suivant. `WavAudioSource.NewTrack()` existe. |
| `GpuPacket` | `SourceActives` à l'offset **120** (octet libre), clampé à `SourceSlots`. `Voices.Actives`. |
| `/profils`, sonde | `actives`, `choix`, `bilans` ; l'export `profils=` s'arrête à `Actives`. |
| `fenetre.py` | lit l'octet 120, n'allume que les cases trouvées, « N sources trouvées ». `stems.py`, `rang.py` acceptent 1 à 8 pistes. |
| tests | `SeparationChoixTests` : trois sources fabriquées → 3, deux → 2 et non 6, rangs au-delà à zéro, `Reset` efface, le bilan montre un coude. **192 verts.** |

### Ce que ça a donné sur Passepartout, en direct

Choix adopté à t = 65 s (40 s de mémoire + le temps du balayage) : 2 → 11,7 % / 0,63 ;
3 → 8,4 % / 0,63 ; 4 → 6,1 % / 0,72 ; 5 → 5,0 % / 0,82. **Quatre sources**, parts
38,5 / 18,3 / 26,7 / 16,5 %, rang effectif 3,77 — contre 2,4 avec six imposées sur 2,7 s.

**Mais** : 69 % de l'énergie sous 150 Hz, 26 % entre 150 et 500, 4 % entre 500 et 2000, et
**ce qui frappe pèse 0,0 %**. Les quatre sont dans le grave. Le bon nombre d'objets, pas
encore les bons objets. Les quatre pistes vérifiées par corrélation de signal contre l'album
(0,61 à 0,86 sur Passepartout, < 0,02 sur tout autre titre) sont dans
`~/Documents/emotion-sources/passepartout/live-1..4.wav`.

> **Règles de livraison qu'il a imposées, et qui restent** : vérifier que c'est bien le bon
> morceau avant de donner un fichier (trois livraisons sur quatre étaient un autre titre, un
> jour) ; flusher les résidus ; une chose à la fois ; **poser les questions avant de coder**.
> Pas de `HarmonicSeparator`, pas de Demucs dans le moteur, pas de nouvel outil « à gauche
> à droite ».

## Le gabarit glissant, porté dans le moteur

Verdict d'oreille sur l'étape 2 : « les 4 lives sont différents certes, mais c'est pas
parfait… quand le xylophone, le kick et le piano sont sur la même mesure, ils s'annulent ».
(Le xylophone était une guitare — Demucs la voit à 4,5 % de l'énergie ; son stem `piano`
sur Passepartout pèse **0,0 %**, il range le piano dans `other`. Un critère « gagne piano »
était donc impossible par construction — corrigé en cours de route.)

### La mesure qui a tout tranché

Kick par kick (275), niveau 100 ms après rapporté à 100 ms avant : live-3 (le medium)
chute de plus de 3 dB à **48 %** des kicks en 150–500 Hz et 45 % en 500–2000, contre 23 %
et 15 % au hasard ; les trois autres montent de +4 à +8 dB dans le medium où le morceau fait
0,0 dB. Puis, à K=12 sur le spectre linéaire : neuf profils sur douze sont les notes de la
basse (pics à 0, 47, 47, 47, 47, 94, 94, 141 Hz…). **Les profils étaient des notes, pas des
instruments.** Racine, cube et KL sur le même modèle : la coupe change de case, jamais de
mécanisme (bancs dans le tmp du job, chiffres dans le README).

### Le modèle

`V(f,t) ≈ Σ_k Σ_p w_k(f−p)·h_k(p,t)` sur 192 cases log (24/octave, C1→C9), gabarit de 144
cases, 49 positions (deux octaves), divergence KL, règles multiplicatives. Mesuré hors ligne
(numpy) : une octave → la basse mange 3 gabarits sur 5 ; quatre octaves → « glisse sur 4
octaves », le modèle triche ; **deux** est la valeur entre les deux. Une forme dans le temps
(10 images) a été mesurée aussi : guitare 0,35 max, kick à égalité avec la basse, 15 min
d'apprentissage par K — abandonnée.

### Ce qui a changé dans le code

| | |
|---|---|
| `ProfileLearner` | réécrit : constantes `ParOctave`, `NLog`, `F0`, `Positions`, `Longueur` ; mémoire trame-major ; `Reconstruire`/`UpdateH`/`UpdateW` en KL avec `Ajouter`/`Produit` en `Vector<float>` ; `Reste` = KL/ΣV ; `Doublon` = pire cosinus **du carré** des gabarits à une translation près (±1 octave) ; `TryStartChoix(…, iterationsBalayage=40, seuilGain=0.15 relatif, seuilDoublon=0.85, plancher=0.01)` ; `TryAdopt(gabarits, positions)`. |
| `SourceSeparator` | prend les **échantillons** (`Feed(samples)`), anneau de 4096, FFT propre, projection triangulaire sur l'axe log (`ConstruireProjection`) ; une image d'apprentissage sur deux ; `Suivre` en KL sur une colonne (8 itérations), rend niveau et **position** par source ; `HauteurOrdonnee` est une vraie hauteur de note (gabarit + position courante) ; ctor `(sampleRate, hop, memoire)` ; le blanchiment est parti avec le profil linéaire. |
| `SpectrumAnalyzer` | `new SourceSeparator(sampleRate, Window, memoire)`, `_separation.Feed(samples)`. |
| `/profils`, sonde | `fenetre=4096, cases, parOctave, f0, positions, longueur, gabarits` (plus de `profils`). |
| `extraire.py` | voie `gabarits` : `axe_log` (la même projection que le moteur, et son retour), `etaler`, `activer_gabarits` (KL), `separer_gabarits` (STFT par blocs, deux passes, masque = part de la source dans la reconstruction, ramené sur les raies). Contrôles 1 et 2 gardés. `stems.py` suit. |
| `fenetre.py` | `EMOTION_PISTES=<prefixe>` joue des pistes externes sous les faders ; plus d'exigence de six pistes ; doublon `journaliser`/`instant` retiré. |
| tests | `SeparationChoixTests` sur des **instruments fabriqués qui changent de note** (basse 55 Hz, piano 220, clair 880, harmoniques propres) : 3 → 3, 2 → 2, une basse seule ≤ 2, ordre du grave à l'aigu, rangs au-delà à zéro, Reset, coude. |
| retiré | `outils/profils_entier.py` (ancien format) ; puis, sur son « vasy », l'ancienne voie `profils` d'`extraire.py` (`separer`, `activations`, le témoin fixe, le masque `independant`, la reproductibilité des rangs) et le second passage de sonde d'`ecouter.sh`. Les mesures restent dans le README. |

### Chiffres du moteur sur Passepartout

Balayage à 40 itérations : 2 → 4,2 % / 0,45 ; 3 → 2,3 % / 0,78 ; 4 → 2,1 % / 0,82 → **3
sources**. Apprentissage 1,3 à 1,6 s en moyenne, 3 à 4 s au pire selon la charge (fil de fond). Extraction 94 s pour
279 s de morceau, reconstruction 151 dB, somme des sources 30,8 dB. Juge Demucs : source1
bass 0,76, source2 bass 0,79, source3 **other (piano) 0,68**, guitare 0,33 max.

> **Le seuil de doublon a coûté un aller-retour** : sur les gabarits bruts, deux sonorités
> distinctes du grave montaient à 0,80 et une copie à 0,85 — pas de marge. Sur le carré des
> gabarits : 0,78 contre 0,98 / 1,00. Avant de régler un seuil, regarder si la mesure a la
> résolution de le porter.

## Étape 3 : le morse de chaque source, et ce que le GPU en fait

Ce qu'il a dit du produit final, à garder tel quel : le GPU « n'a que faire de si c'est un
piano ou un xylophone ou un synthé ou un tambour, il a besoin d'information pour
modifier/générer des formes géométriques ». Le BPM fait tourner le cube ; **le morse d'une
source** (`.- -. .-.`) change sa couleur ou le spectre d'une vidéo ; un souffle étire ses
coins. Par source, il faut donc : **quand ça frappe, comment ça tient, comment ça enfle** —
jamais un nom. Le son ne passe **jamais** par le moteur : platine → Xone:92 → sono ; le
moteur écoute et publie des données. (J'avais proposé des faders audio dans le moteur :
faux, retiré ; les pistes WAV extraites restent un outil de vérification, pas le produit.)

### La mesure qui a ouvert l'étape

Sonde avec `sources=<tsv>` (nouveau : niveau, frappe, pique, tenue par source et par
image). Juge : les attaques des stems Demucs (montée de 6 dB en 30 ms de l'enveloppe à 5 ms,
100 ms d'écart). Sur Passepartout après le choix (59–278 s), **les trois sources rendaient
trois morses quasi identiques** — 815, 893, 878 frappes — au niveau du hasard : drums 28 /
32 / 28 % (hasard 25), bass 49 / 45 / 47 (hasard 44), other 25 / 26 / 26 (hasard 23). Cause
dans le code : `etats[i] = brut with { Level, Position, Heard, … }` où `brut =
_voices.EtatDe(i)` — **le bit `Hit` restait celui du registre de fréquence de même rang**,
le seul champ que la séparation ne remplaçait pas.

### La frappe tirée du niveau de la source

Prototypée en Python sur le niveau publié (déjà rapporté à sa crête) : frappe = montée ≥
0,15 en une image, niveau ≥ 0,35, repos de 4 images. Basse : **83 % des frappes sur une
attaque réelle**, contre 44 % au hasard (le décalage a été balayé de −80 à +100 ms : le
maximum est à 0 / −20 ms, pas de retard à corriger). Portée dans `SourceEnvelope.Frappe`
(`SeuilFrappe`, `PlancherFrappe`, `ReposFrappe`), branchée dans `SpectrumAnalyzer` (`Hit =
_enveloppes.Frappe(i)`). Après : source 1 → bass 83 % (drums 1 %), source 2 → bass 65 %,
source 3 → other 29 % (hasard 21). Tests `SourceEnvelopeFrappeTests` (4). 198 verts.

> **Le piano n'a pas de juge automatique.** Même `moteur-3.wav`, validé piano à l'oreille,
> ne s'accorde qu'à 34 % avec les attaques de `other` (hasard 23) — le stem contient des
> nappes et le piano lo-fi est noyé de réverb. Restreindre à 200–2000 Hz ne change rien.
> **C'est la touche espace qui juge** : isoler la case du piano, remettre tout, taper aux
> touches entendues, Échap → `~/Documents/emotion-sources/rapports/<morceau>-<date>.json`
> (marques + journal du moteur, dont `frappe` par image). À lire et à confronter.

### Le morse n'a pas pu être jugé : les pistes en solo n'étaient pas celles du paquet

Trois rapports de marques (31, 65, 46 marques), tous inexploitables, et la cause était dans
la fenêtre : `stems.py` extrayait ses pistes **au provisoire** (`pret`), pas au choix
(`choix`). Le DJ isolait la case 1, entendait une piste provisoire (du piano, 0,34 avec
`other`), et le paquet publiait la basse dans cette case-là après 45 s. Corrigé : on attend
`choix`. Puis les pistes arrivaient à 2 min 20 (choix à 65 s + 94 s d'extraction) et il
fermait avant, deux fois : le choix part maintenant **dès la mémoire pleine** (40 s), et
l'extraction passe à 75 % de recouvrement et 15 itérations (35 s, juge inchangé). Bout en
bout, moteur + `stems.py` : pistes vers 70–80 s.

### La croissance : le morceau ne dit pas tout en quarante secondes

Le choix à 40 s a rendu **2** sources — et c'était juste : Passepartout commence par piano +
basse (0–30 s), la guitare entre à 30 s, la batterie à 50 s, et le piano *disparaît* de 40 à
80 s (énergie des stems Demucs par tranche de 10 s). Mais rien ne grandissait ensuite.

`ProfileLearner.TryStartCroissance` : à chaque réapprentissage (toutes les 20 s), on
apprend à K depuis les gabarits courants (ils restent à leur place), puis à K+1 avec un
gabarit neuf, et l'on garde K+1 s'il passe les critères. **La croissance ne réordonne pas** :
la nouvelle source prend la case suivante. On ne redescend jamais.

Les critères ont dû être recalés, parce que les instruments fabriqués grandissaient à tort
(2 → 3, 3 → 4) : un instrument coupé en deux gabarits de formes différentes passait le
doublon. Lu dans `Historique` (tous les bilans depuis le début, nouveau) :

| | doublon | lien |
|---|---|---|
| fausses croissances (fabriqué) | 0,74 · 0,79 | 0,59 · 0,69 |
| vraie entrée de la guitare, Passepartout 60 s | **0,34** | **0,55** |
| seuils | **0,60** (était 0,85) | **0,70** (nouveau) |

Le **lien** est la corrélation dans le temps des niveaux de deux sources : un instrument
coupé en deux donne deux niveaux qui montent et descendent ensemble. Résultat sur
Passepartout : 4 (provisoire) → **2 à 40 s → 3 à 60 s**, stable ensuite ; la 4ᵉ candidate
(la batterie) est rejetée à chaque essai à doublon 0,90–0,94. Juge sur les pistes finales :
source 1 basse 0,76 ; source 2 guitare 0,43 / other 0,44 ; source 3 other 0,53.
Apprentissage 2,4 s en moyenne, 4 s au pire, 13 fois sur le morceau.

> **Ce que les rapports de marques ont quand même dit** : il tape une fois toutes les
> ~2,4 s, et ses marques ne suivent aucun juge automatique. Quand le protocole sera enfin
> propre (pistes = paquet, dès 80 s), il faut lui demander CE qu'il tape avant de lire.

### Le reste est une case, et c'est le kick

Son oreille sur les trois pistes du moteur : « le boom-tchak, on l'entend sur toutes les
trois ». Mesuré : le kick n'a pas de gabarit (trois bancs), et le masque au prorata
répartissait son énergie entre toutes les sources. **Ce que les gabarits n'expliquent pas,
mis dans une piste à part**, corrèle à **0,77** avec `drums` de Demucs (0,55 au mieux
avant, collé à la basse), et la basse s'en nettoie (0,76 → 0,83). Il l'a entendu : « on
entend le boum-tchak sur source 4, c'est très bien ».

| | drums | bass | guitar | piano |
|---|---|---|---|---|
| source 1 | 0,40 | **0,83** | 0,11 | 0,03 |
| source 2 | 0,12 | 0,36 | 0,44 | 0,45 |
| source 3 | 0,07 | 0,03 | 0,29 | **0,54** |
| **le reste** | **0,77** | 0,25 | 0,20 | 0,26 |

Dans le moteur : `SourceSeparator.Publiees = Actives + 1`, `RangReste`, niveau = Σ max(V −
V̂, 0) sur l'image, hauteur = son centre de gravité, frappe par `SourceEnvelope` comme les
autres, stabilité 1. Le paquet compte le reste dans `SourceActives`. `extraire.py` et
`stems.py` écrivent une piste de plus (les masques somment toujours à un : 151 dB / 30,8 dB).

**Sans lissage, les gabarits absorbaient le kick en direct.** Six octaves de large sur 49
positions, ils expliquent un coup plat presque aussi bien qu'une note : la solution KL d'une
image sautait pour l'avaler (niveau du reste à 0,43 avec la batterie, contre 0,77 hors ligne
où `lisser` étale les niveaux sur huit trames). Le suivi lisse donc ses niveaux avec la
constante de temps de la fenêtre (`LissageSuiviS = 85 ms`) : 0,65 en direct, et la case 1
perd sa batterie (0,31 → 0,13).

**Le morse du reste est en retard de 60 à 80 ms, et il est juste** : décalé de −80 ms, le
bit `frappe` de la case reste tombe sur une attaque réelle de la batterie à **88 %** (hasard
22 %) ; sans décalage, 4 %. Le retard vient de la fenêtre de 4096 (centre à −42 ms) et de la
règle de montée. À compenser côté rendu (la fenêtre a déjà `EMOTION_AVANCE_MS`), pas à
cacher. La règle de frappe sur le reste supporterait des seuils plus hauts (pente 0,3 sur 3
images : 96 %, rappel 56 %) — à décider avec le caractère.

> Le test fabriqué du reste a d'abord échoué parce qu'il lisait le niveau **à l'image du
> coup** : sous la fenêtre de Hann, le bloc qui vient d'arriver pèse presque zéro. Le coup
> pèse deux à cinq blocs plus tard. Une latence de fenêtre n'est pas un bug, mais elle se
> mesure avant de juger.

### Validation croisée : les onze titres de l'album, le moteur seul

Tout avait été calé sur Passepartout ; le risque était de l'avoir appris par cœur. Banc : le
moteur apprend seul sur chaque titre décodé (choix à 40 s, croissance, reste), la sonde
exporte ses gabarits, `extraire.py` écrit les pistes, et l'on prend la **meilleure
corrélation d'une piste avec chaque stem Demucs** (mélodique = tout sauf drums et bass).

| titre | pistes | basse | batterie | mélodique |
|---|---|---|---|---|
| Interactive WordBank | 4 | 0,86 | 0,78 | 0,91 |
| ThinkMap Module | 4 | 0,67 | 0,73 | 0,75 |
| Glyph Chamber | 4 | **0,96** | 0,60 | 0,80 |
| Dead Internet Theory | 4 | 0,92 | 0,69 | 0,77 |
| Passepartout | 4 | 0,83 | 0,77 | 0,72 |
| Echoes of the Ancients | 4 | 0,83 | 0,80 | 0,80 |
| Timeline Explorer | 4 | 0,71 | 0,80 | 0,83 |
| Codex Sinaiticus | 5 | **0,51** | 0,78 | 0,82 |
| HyperText Odyssey | 4 | 0,86 | 0,54 | 0,90 |

Neuf titres jugés (NeoAtlas et Lost Cultures n'ont pas de référence Demucs en cache). Sur
**sept sur neuf, la batterie est la dernière piste — le reste** ; Codex Sinaiticus et
HyperText Odyssey la mettent dans un gabarit. Basse médiane 0,83, batterie 0,77, mélodique
0,80 : **Passepartout n'était pas un cas heureux, c'est le régime de l'album.** Le plus
faible est la basse de Codex Sinaiticus (0,51, cinq sources) — à écouter. Pistes livrées
pour l'oreille : `~/Documents/emotion-sources/{glyph-chamber,codex-sinaiticus,timeline-explorer}/`.

### Le motif de chaque source, et le verrou

Sa phrase, à garder telle quelle : « pendant le beatmatch il arrive à séparer le son ; au
master il doit se débrouiller : fixer le BPM, ne plus retoucher une fois le boom-tchak
capté, et si au boom une note de piano puis au tchak une autre, repérer les patterns de
répétition ». Le cue apprend, le master joue.

**Mesure d'abord** (sonde `sources=` avec la phase, trois titres, seize cases par mesure) :
le bit `frappe` donne des motifs vides (trop peu de frappes depuis le lissage du suivi, une
case de jitter) ; le niveau suit la nappe ; **la montée du niveau** donne des motifs stables
à 0,93–0,98 entre mesures paires et impaires sur Glyph Chamber, et le motif du reste y colle
à celui de la batterie ; Passepartout 0,4–0,8 ; Timeline Explorer rien, pas même chez le
juge — la grille y dérive, et la stabilité du motif devient un indicateur de santé de la
grille.

**Dans le moteur** : `MotifSources` (montée par case, seize mesures en anneau, stabilité =
corrélation paires/impaires, `Masque` = cases ≥ 50 % du max, `Verrouille(publiees)` = deux
sources tenues à ≥ 0,80 sur seize mesures). Branché dans l'analyseur sur `act[i]` et
`(_grid.Beat + _grid.Phase) / 4`. Paquet : six `ushort` à **243** (`MotifSource0..5`), verrou
à **255**. `SourceSeparator.Verrou` coupe tout réapprentissage. La fenêtre dessine seize
cases sous chaque forme avec le curseur de la mesure (`P_MOTIFS`, `P_VERROU`). La sonde
imprime motifs, stabilités, verrou.

> **Le verrou souple a été essayé et retiré** : gabarits figés mais croissance permise →
> sans adaptation, « du neuf » apparaissait à chaque essai, Glyph Chamber montait à cinq
> sources. Strict, avec seize mesures (≈ 50 s) avant de verrouiller, pour laisser aux
> instruments qui entrent le temps d'avoir leur case. Glyph Chamber : 3 + reste, verrouillé,
> motifs 0,86–0,93. Passepartout, Timeline Explorer, WordBank : pas de verrou.

Le README a été réécrit en tête pour cette avancée (cas d'usage, séquence cue→master, ce que
le rendu reçoit par source, tableau croisé), avec deux captures `docs/images/mur.png` et
`motif.png` prises sur Glyph Chamber verrouillé (fenêtre hors écran, `capture.sh` dans le tmp
du job).

### Le caractère par source : frappe, pincé, tient — sur la durée

Mesuré sur les niveaux publiés (trois titres, régime final) : la `tenue` du moteur (moyenne
rapportée à la crête glissante de 1,5 s) moyennée dans le temps sépare nettement — le reste
0,22–0,38, les nappes et la basse 0,84–0,92, le pincé (piano de Passepartout, troisième
source de Glyph Chamber) 0,65–0,77. `SourceEnvelope.Caractere` = cette tenue moyennée sur
**20 s** (`CaractereS`), apprise seulement quand la source joue. Paquet : **un quartet par
source à 201–203** (`Caracteres0..2`, source 0 dans le quartet bas de 201). `Voices.Caracteres`.
La fenêtre lit l'étiquette « frappé / pincé / tenu » là, plus sur l'image (une nappe qui
montait une fois passait pour frappée). La sonde l'imprime. Passepartout : 0,70 / 0,89 /
0,93 / reste 0,35 ; Glyph : 0,86 / 0,89 / 0,77 / reste 0,32. Trois tests. 208 verts.

### La tonalité du crate est-elle un prior de confiance ? Mesuré

Sa question : « savoir que c'est 9A de la part du crate peut nous aider, comme le BPM ? ».
Mesure avant tout. Un premier juge maison (chromagramme naïf + profils de Krumhansl) ne
retrouvait la fiche que 1 fois sur 10, avec des marges nulles — et a révélé que **quatre
titres sont désaccordés d'un quart de ton** (−48 cents : Glyph Chamber, Passepartout, Codex
Sinaiticus, Lost Cultures), ce qui met leurs notes exactement entre deux cases de l'axe
(24 par octave = 50 cents). Le **chromagramme du moteur** (`Harmony.Chroma`, exporté par la
sonde avec `chroma=<fichier>`, moyenné sur 150 s) fait mieux :

| | exact | relatif | voisin (quinte) | non |
|---|---|---|---|---|
| sur 10 titres avec fiche | 4 | 1 | 1 | 4 |

Exacts : WordBank 2A, Dead Internet Theory 4A, Echoes 11A, Timeline 7A (marges 0,05–0,26).
Les « non » sont des cas serrés en lo-fi (Passepartout : classes A G D C, la fiche 8A est
plausible, Krumhansl a pris 10B). **Verdict : la fiche est un prior utilisable, pas une
certitude** — à employer comme le BPM, en préférence douce et jamais en contrainte, avec
la contradiction dite quand le chromagramme n'est pas d'accord. Deux suites possibles,
mesurables : estimer le désaccordage au cue et décaler l'axe log d'autant (sans fiche) ;
puis une préférence de gamme sur les positions des gabarits et la publication du **degré**
joué par chaque source (« au boum la tonique, au tchak la quinte »), qui survit aux
transitions Camelot.

### L'accordage du disque, relevé au cue

Quatre titres sont désaccordés d'un quart de ton et l'axe a 50 cents par case. Le
séparateur relève donc, sur les premières secondes (le provisoire, 5,5 s), où tombent les
pics du spectre (80–2000 Hz) à l'intérieur du demi-ton — histogramme pondéré par 5 cents —
et **décale son axe log d'autant** avant d'apprendre (`RelevierAccordage`, `Accorder`,
`ConstruireProjection(cents)`, `AccordageCents`, `F0`), en vidant la mémoire projetée sur le
mauvais axe. Sous 15 cents, rien ne bouge. Export : `f0` (décalé) et `accordageCents` dans
`/profils` et la sonde, pour qu'`extraire.py` rebâtisse le même axe.

**Le pic s'interpole, sinon l'estimation est du bruit.** Première version sans
interpolation : +23 cents sur Passepartout là où le juge hors ligne disait −48, et un
disque fabriqué désaccordé de +40 relevé à +27 — une raie fait 11,7 Hz, soit 46 cents à
440 Hz, presque une case. Le commit `b9e3af8` est parti avec « 212 tests verts » dans son
message alors que deux échouaient : la chaîne a commité sans lire le résultat. Corrigé par
une parabole sur les trois raies du pic ; les deux estimateurs sont alors d'accord sur les
5,5 premières secondes (Passepartout −43 / −37, Glyph +43 / +42, Codex −48 / −42, Lost
Cultures −43 / −42, WordBank 0 / −7).

Mesuré sur cinq titres, avant → après : Glyph mélodique 0,80 → **0,87**, Passepartout
batterie 0,77 → 0,80 (mélodique 0,72 → 0,65), Codex **5 → 3 sources** (la basse n'est plus
coupée en trois, 0,51 → 0,52), WordBank inchangé (axe standard). Motifs inchangés. **Gain
modeste, aucune régression** — gardé. `AccordageTests` (4). 212 verts, fumée 32 ok.

### La gamme de la fiche : le prior n'apporte rien, le degré se publie

(b) après la mesure de tonalité. `Gamme.cs` : Camelot → tonique, mode, sept classes ; degré
d'une classe (0 tonique … 6, 7 hors gamme, 15 inconnu). Plomberie : `Signal__Camelot` côté
serveur, `cle=9A` côté sonde, `voir.sh` lit `key` dans le crate, `/profils` publie `gamme` et
`accordGamme`.

**Le prior doux sur les positions (0,7 hors gamme) a été mesuré et retiré** : juge Demucs et
motifs identiques à la décimale sur cinq titres. Une position reste libre.

**Le degré par source** (paquet : un quartet à **209–211**, même rangement que les
caractères ; accord de la fiche à **215**) se lit sur la **reconstruction de chaque source**
sur l'axe log, repliée en chromagramme, **seulement là où l'axe résout le demi-ton** (au-
dessus de ~200 Hz à 48 kHz : dans le grave une raie de 11,7 Hz étale un la jusqu'au la#), et
avec une **marge** : la classe gagnante doit peser 1,3 fois la suivante, sinon « inconnu ».
Trois versions ont été jetées avant : lire le fondamental du gabarit (faux dans le grave),
replier le gabarit puis décaler d'une position moyenne arrondie (glisse d'un demi-ton). Test
`DegreParSourceTests` : trois instruments fabriqués qui parcourent la mineur puis se posent
sur la, do, mi → le piano rend III et le clair V ; la basse, sous la résolution, a le droit
de s'abstenir, jamais de se tromper (forcée, elle tombait un demi-ton à côté).

**Ce qu'il faut dire** : sur les titres réels, au dernier instant de la passe, la plupart des
cases rendent « inconnu » ou « · » (hors gamme) ; la fiabilité des degrés n'est établie que
sur le signal fabriqué. L'accord du chromagramme avec la gamme vaut 0,59–0,84 pour un
plancher de hasard à 0,58 (7 classes sur 12) : c'est un indice faible, la mention « fiche
contredite » (< 0,6) est à prendre comme telle. La fenêtre affiche le degré dans le titre de
la case (I … VII, ·). 224 tests verts, fumée 32 ok.

### Le rendu par gestes, et le coup annoncé avant d'être entendu

Sa demande : « on a réussi à les séparer ; là c'est des formes géométriques qui bougent
tellement dans tous les sens que l'œil n'arrive pas à suivre si la forme a bougé au bon
moment ». Et : « une boule qui devient grande quand le boom arrive et un triangle quand
c'est le tchak ; pour les sons tenus des vagues qui oscillent plus quand le son est là ; ce
qui est frappé, un truc qui se voit ». La fenêtre dessine maintenant **un geste par
caractère** (classe `Geste`, `dessiner_geste`), à la place des formes en caractères :

| ce que la source est | le geste |
|---|---|
| le reste (dernière case) | une **boule** qui grossit sur le boom (hauteur du reste < 0,33), un **triangle** qui claque sur le tchak |
| tient (caractère > 0,8) | trois **vagues**, dont la vitesse et l'amplitude suivent le niveau |
| pincé (0,45–0,8) | une **corde** tendue qui vibre au pincement et s'amortit (0,45 s) |
| frappe (< 0,45) | un point qui pèse le niveau, et une **onde d'impact** qui s'ouvre (0,35 s) |

**Le retard du bit `frappe` est compensé par le motif.** Le bit arrive 60–80 ms après
l'attaque ; le motif de la source, lui, est en avance. Quand la mesure entre dans une case
que le motif allume, le geste part (« annoncé ») ; un bit qui arrive dans les 150 ms ne fait
que confirmer ; un bit sans annonce déclenche quand même, en retard. Pour le reste, chaque
case de la mesure retient ce qu'elle a fait entendre (boom ou tchak, par la hauteur au
moment du bit) : l'annonce à cette case rejoue le bon geste. Les formes en caractères
(`formes.py`) restent pour les autres panneaux. Captures refaites sur Glyph Chamber.

### La fuite : ni porte ni binaire — la dominance, en deux couleurs

Son oreille sur les pistes : « il laisse passer en cachette quelques sons à partir d'un
certain nombre de décibels ; on entend le reste en arrière et ce qu'il y a en avant ». Sa
proposition : un seuil de volume, « on arrondit, 20 % → 0, 80 % → 100 % ». Mesuré sur
Passepartout 60–150 s (fuite = corrélation d'une piste avec les stems qui ne sont pas les
siens) :

| variante | basse (piste 1) | fuite | le reste (piste 4) | fuite |
|---|---|---|---|---|
| prorata (le moteur) | 0,78 | 0,43 | 0,41 | 0,13 |
| porte à 10 / 20 / 35 % de la dominante | 0,75 → 0,69 | **0,45–0,48** | 0,36 | 0,08–0,13 |
| binaire (dominante seule) | **0,34** | 0,24 | 0,39 | 0,04 |

**Les portes ne changent rien** : la fuite n'est pas dans les raies faibles, elle est dans
les raies **partagées à parts presque égales** (basse et kick dans les mêmes raies graves).
**Le binaire coupe la fuite et tue la basse** (0,78 → 0,34) : à chaque kick, le kick gagne
la raie et la basse disparaît — le « ça s'étouffe » du matin, en pire, avec du grain.

Sa relance : « pourquoi pas afficher dans une autre couleur la partie discrète ? » et « le
GPU saura différencier les deux si on met un point à ce qui est extrait ». C'est la bonne
réponse : couper décide à la place de l'œil, colorer lui dit ce qu'on sait. Le séparateur
mesure par source **la dominance** — la part de son énergie dans des cases dont elle est
propriétaire (≥ 50 % du total) — et le reste est sa part discrète. Publiée dans les **bits
1–3 du drapeau** de la case (`SourceDominanceShift`, huit crans ; `LaneState.Dominance`,
`DominanceOrdonnee`). Passepartout : piano 0,62, synthé 0,94, aigus 0,89, reste 0,65. La
fenêtre dessine chaque geste **en vert à hauteur de sa dominance, en ambre pour sa part
discrète** (une sous-couche plus large), et **deux points** en haut de la case : le vert
grossit avec ce qui est extrait, l'ambre avec ce qui est partagé. Les variantes à porte
restent dans `variantes/` pour son oreille. 224 tests, fumée 32 ok.

Reste de l'étape 3, pas fait : le morse du piano jugé à l'oreille, avec le protocole
corrigé.

### Le relais complet : le cue apprend, le master ne fait que suivre — les deux disques

Sa vision, le 15 septembre : « l'analyse du cue et l'analyse du master, c'est deux choses
qui s'exécutent en parallèle. L'un fixe les règles, l'autre analyse en temps réel avec déjà
toutes les informations. Si j'enlève les basses de A, le master saura que les basses de A ne
sont pas là ; le tchak côté A et la basse côté B tournent en même temps, l'image suit le
fade. » Décision : **huit cases partagées**, quatre par disque au plus, un reste partagé.

| pièce | ce qu'elle fait |
|---|---|
| `RoleAnalyseur` (Solo · Cue · Master), `SpectrumAnalyzer.Role` | en Master, `SourceSeparator.SuiviSeul` : aucun portrait ne se forme, `Memoriser` range et sort. Sans cue configuré, Solo = comme avant. |
| `SourceSeparator.Empreinte` / `Portrait()` | gabarits dans l'ordre des rangs, positions, hauteurs, centres, stabilité, vues, accordage, fiche |
| `AdopterPortrait`, `AccueillirPortrait`, `RetirerDisque`, `Compacter` → `Relais(AncienRang, RangEntrant, Actives)` | le suivi joint : les gabarits de B s'ajoutent à ceux de A (`_disque` par ligne, `DisqueOrdonne`, `EnFondu`) ; A est réduit aux plus entendus si la place manque ; le retrait compacte et retague 1 → 0. Les positions de B sont décalées de (cents_B − cents_A)/50 cases ; les gabarits ne bougent pas, ils glissent. |
| `SpectrumAnalyzer.EmpreinteDisque`, `Empreinte()`, `Accueillir()`, `Retirer()`, `Deplacer()` | l'état par rang suit le relais : `MotifSources`, `SourceEnvelope`, `ContourRange` ont `Exporter/Importer/Vider/Reorganiser` ; les crêtes aussi. Le reste garde le sien pendant le fondu et prend celui du disque entrant au retrait. |
| `DualAudioSource` | accueil à 0,15 de fondu, tempo à 0,5, retrait à 0,9 (+ `NewTrack()` du cue), `Phase` pour le journal ; les rôles sont posés dans le constructeur |
| paquet | `Sources = 8`, `SourceCount = 8` ; drapeau bits 4–5 = **platine** (`SourcePlatineShift` : 1 ou 2, 3 partagé, 0 inconnu — pas « joue/entre », qui change de sens au retrait et ferait sauter une scène de côté) ; octet 60 = `Relais` (bits 0–1 platine qui joue, bit 2 fondu en cours, bits 4–5 platine qui entre) ; octet 121 = pitch signé en quarts de % ; motifs 6–7 à 122/124, caractères 3 à 126, degrés 3 à 127 ; octet 255 = bit 0 verrou sonorités, bit 1 verrou rythme. `SpectrumAnalyzer.PlatineJoue/PlatineEntre` : le premier disque arrive sur la platine 1, les suivants alternent, le retrait garde la platine |
| enregistrer et rejouer | sonde `paquets=<f.pak>` (les paquets bout à bout, `TrackContext.Silence`) et `probe rejoue <f.pak> [vitesse]` (écrit dans l'anneau à la cadence des `TimeMs`) ; `outils/fondu.sh A B` fabrique le fondu si besoin, joue le mélange et ouvre la fenêtre dessus. C'est la matière du rendu tant qu'il n'y a pas de table. |
| `MotifSources.VerrouRythme` (4 mesures), `SpectrumAnalyzer.RythmeSu` | le verrou à deux vitesses : le rythme resserre `TempoTracker.Preferer` autour du BPM mesuré |
| `fenetre.py` | **une scène par platine** : les cases signées P1 à gauche, P2 à droite, le reste partagé à cheval sur la frontière ; l'ouverture de chaque scène est lissée (0,4 s), P1 reste toujours à gauche de P2 ; une barre par scène montre le fader tel que le rendu le voit (niveau moyen des cases) ; `outils/fondu_images.py` rend huit instants d'un fondu enregistré hors écran |
| sonde `relaisA= relaisB= fondu=t0,t1`, `outils/relais.py` | la mesure : cue A, cue B, master sur le mélange ; juge = stems Demucs des deux disques pesés par le fader, par tranche (A seul / fondu / B seul) |

**Mesuré sur deux couples** (WordBank → Echoes, Dead Internet → Glyph) : pendant le fondu,
**9 cases sur 9** ont pour stem le plus proche un stem de leur disque ; la somme des cases
de A suit le fader de A à 0,71 / 0,76, celle de B le fader de B à 0,55 / 0,85 ; la
corrélation avec l'autre disque est négative (médiane −0,43 / −0,17). Sur les instruments
fabriqués (`RelaisTests`), un master qui adopte suit comme le cue à > 0,9, et les cases de A
tombent à un tiers pendant que celles de B triplent — à la fuite près, mesurée ailleurs à
0,4 sur du vrai son : c'est pour cela que le test n'exige pas zéro.

**Le paquet était plein, et deux champs se chevauchaient déjà.** En cherchant un octet
« libre » pour l'état du relais, un compte exact des tailles (les blocs, pas le nombre de
champs) a donné zéro octet libre et **une collision à 215 : `AccordGamme` écrasait
`EventSharp`** depuis le commit de la gamme. Les douze bandes étaient douze flottants de 48 à
95 que **personne ne lisait comme tels** : la fenêtre et l'outil ASCII lisaient douze octets
depuis toujours, donc l'octet bas d'un flottant. Elles sont maintenant douze octets (48–59),
l'état du relais est à 60, l'accord de gamme à 61, et 62–95 sont libres pour la suite. Un
niveau qui pilote un visuel n'a jamais eu besoin de trente-deux bits.

**Pièges traversés.** `Sources` passé de 6 à 8 a cassé un test du paquet qui donnait une
forme `6 − i` (0 pour la case 6, remplacé par la forme par défaut) ; la stabilité des motifs
demande quatre mesures (deux paires, deux impaires), donc le verrou rythme est à quatre et
non trois ; un `record Empreinte` et une méthode `Empreinte()` ne cohabitent pas ; `Pitch`
existait déjà dans le paquet (la classe de hauteur) → `PitchRelais` ; en zsh, une variable
non citée ne se découpe pas en arguments.

**Le pitch, mesuré sur le mélange** (`relais.py`, Dead Internet 91 → Glyph 74, faders
immobiles) : 0,0 % avant, −0,2 % en début de fondu, −0,5 % après. Deux pièges corrigés pour
y arriver : `AdoptTempo` recentre maintenant la préférence sur le tempo adopté (sinon le verrou
rythme la laissait autour de 91 et le suivi retombait à 97, le triolet de 74 : +31 % publiés
sur un fader immobile) ; et la référence du pitch bascule sur le disque entrant à la mi-fondu,
quand son tempo est adopté (sinon −19 % pendant dix secondes).

**Pas fait** : le pitch et l'EQ sur un vrai fader poussé ne sont pas mesurés, faute de table ;
le reste partagé ne distingue pas les deux batteries (le motif de chaque reste, transmis, le pourrait
côté rendu) ; en Master sans cue passé, aucune source n'est publiée tant qu'aucun portrait
n'est arrivé — c'est voulu.

**Le passage refait comme le DJ le fait (17 septembre)** — `docs/mix-vinyle.md` pour le
geste, `outils/relais.py` pour le fabriquer (fiches du crate : BPM joué, clé transposée ;
grille vérité pour caler B sur A ; gestes sur les 1 ; `cue=180` et `blend=`/`fader=` dans la
sonde). Deux résultats qui changent la suite, mesurés sur Codex ↔ Passepartout (8A tous deux,
le couple que le crate classe en tête) : **les cases de A doublent quand B entre à fader A
constant** — en mix harmonique les gabarits glissants de A expliquent les notes de B ; et
**`BlendEstimator` sature à 0,9 quand B est à fond alors que A joue encore** : il mesure la
présence de B, pas l'absence de A. Le fondu linéaire de 20 s ne testait rien de tout cela.
Pistes ouvertes, à trancher avec lui : lire les cases de B **au casque** (le cue continue
d'analyser B pendant le passage, ses niveaux sont exacts, le master n'a qu'à retrancher
g·cue) ; piloter le retrait par ce qui reste de A une fois B retranché.

**Le fondu rejoué s'entend sous les faders, et l'image suit la main (17 septembre).** La
sonde exporte les gabarits du master à chaque phase du relais (`profils=` → `-1/-2/-3.json`),
`relais.py` extrait le son de chaque case phase par phase et coud une piste par case (20 ms
de creux aux coutures, somme = mélange à 24 dB), `fondu.sh` les fait jouer par la fenêtre
sous ses faders au lieu de `paplay`. Et le fader de case agit sur l'image : le niveau dessiné
est le niveau publié fois le gain, la case l'écrit (« ×0,4 »). C'est un réglage de ce qui
affiche, comme l'avance — rien ne réanalyse. Son idée : « un flag qui indique comment l'image
doit afficher en fonction du curseur ».

**La scène par platine (16 septembre)** est la spécification du GPU jouée sur le mock, et
elle a fixé deux règles : la composition se fait sur le **tag de platine** des cases, jamais
sur « joue / entre » (qui changent de sens au retrait) ; et **toute ouverture ou fermeture de
scène est une grandeur lissée**, pas un état, pour la même raison que le curseur de mesure ne
saute plus. Deux détails coûtent cher si on les oublie : dans une case à moitié de largeur, le
titre se coupe à la place que laisse le verdict, en lâchant les mots de la fin et en gardant
le rang avec sa platine entier (« 5 P1 » tronqué de « 5 P1+P2 » dirait le contraire) ; et
le harnais de rendu hors écran remplace `time.monotonic` par l'horloge des paquets, sans quoi
le lissage ne bouge pas entre deux appels.

## Le cue sur les voies de la table, et le relais qui ne se faisait qu'une fois

Une DJM sur USB (la 750MK2 du studio, 26 septembre 2026) remonte chaque voie **avant son
fader** et le Rec Out — jamais le casque. Le cue du moteur est donc la voie du deck qui se
prépare, et le moteur sait laquelle sans rien mesurer : **avec deux platines, le cue est sur
l'autre voie après chaque relais** (`CueAlternant`). Le crate dit quel disque est au casque
(la fiche), la table dit sur quelle voie ; les deux ne se confondent pas. Un filet permute
si la voie prise pour le cue est dans le master trois secondes hors relais — c'est le cas
au départ quand on s'est trompé de sens, et jamais pendant un vrai fondu, qui monte depuis
zéro avec le relais engagé avant 0,8. `Signal__CueDevice=djm_2,djm_1` : deux noms, la
première porte le cue au départ.

**En sortant les trois temps du relais de la boucle de lecture** (`MachineRelais`, neuf
tests) un défaut est apparu : après le retrait, rien ne remettait la machine à zéro. Le
premier passage de la soirée se relayait, les suivants passaient sans accueil ni retrait —
un set n'avait droit qu'à un relais. `relais.py` ne fait qu'un fondu et ne pouvait pas le
voir ; `Dix_disques_dix_relais` le garde. Le quatrième temps, **libre**, arrive quand le cue
sort du mélange (< 0,1), c'est-à-dire quand un autre disque tourne au casque — pas avant,
sinon le même disque serait accueilli deux fois.

Le plan de la session, écrit avant elle avec les résultats attendus : `docs/studio-2026-09-26.md`.

## Le contrôle de fumée, et pourquoi il a fallu l'écrire

```sh
./outils/fumee.sh
```

**Trois fois dans la même journée, une fonctionnalité a été annoncée prête et découverte
cassée au premier lancement** : le son qui ne sortait de nulle part, la sélection qui coupait
le mélange, la fiche vide qui empêchait le serveur de s'ouvrir. Les 187 tests (192 aujourd'hui) étaient verts à
chaque fois, et les rendus hors écran aussi.

> **On vérifiait le code modifié, jamais la commande tapée.** Un défaut de câblage ne vit
> dans aucune unité ; il vit *entre* elles, exactement là où un test unitaire ne regarde pas.

Ce script ne teste aucune logique. Il tape ce que le DJ tape et regarde si ça démarre : le
moteur dans chacun de ses modes, l'import de chaque outil, les réglages facultatifs laissés
vides ou absurdes, et la fenêtre qui rend une image sans lever.

### Ce qu'il a trouvé à son premier passage

| | |
|---|---|
| **`Signal__Bpm=` vide** | `GetValue<float>` lève sur une chaîne vide — le serveur refusait de s'ouvrir pour une valeur **facultative**. Cassait `--direct` et tout morceau absent du crate. Deux endroits la lisaient ; corriger le premier laissait le second échouer pareil. |
| **`EMOTION_AVANCE_MS=` vide** | même famille, côté fenêtre. Tous les réglages passent maintenant par `reglage()`, qui rend le défaut plutôt que de tomber. |
| **`metronome.py` écrivait un WAV à l'import** | tout son corps était au niveau du module. Un module qui produit un fichier en étant chargé ne peut ni se relire ni se réutiliser. |
| **le dépôt acceptait les œuvres** | la règle « aucune œuvre dans le dépôt » était écrite mais rien ne l'appliquait : un `git add -A` aurait versé un morceau du bac sur un dépôt **public**. Le `.gitignore` la fait respecter. |
| **des chemins de session figés** | `banc.sh` et `motif.py` pointaient un répertoire temporaire d'un jour donné, dans une vitrine d'entretien. Ils lisent `EMOTION_BAC`. |

**Une variable vide n'est pas une variable absente.** C'est la leçon commune aux deux
premiers, et elle vaut d'être retenue : un script qui n'a rien à dire écrit une chaîne vide,
pas rien du tout.

## Façon de travailler

Questions ciblées avant de partir sur une solution. Mesurer avant de corriger, et écrire
la mesure dans le commit. Pas de refactor non demandé, pas de nouvelle dépendance sans le
dire. Commits petits, nommés par fonctionnalité, en français sans accents.

**Ce dépôt est une vitrine.** Le DJ le présentera en entretien comme le projet dont il est
le plus fier. Le README doit donc défendre chaque choix, y compris les échecs — et ne
jamais réclamer un motif d'architecture qu'il n'applique pas.
