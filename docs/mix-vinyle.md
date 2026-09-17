# Le mix vinyle, tel que le rendu doit le comprendre

Ce que le projet doit savoir du geste du DJ avant de simuler un passage d'un disque à
l'autre. Rassemblé le 17 septembre 2026 à partir de guides de DJ (sources en bas) et des
consignes du DJ lui-même ; à corriger par lui là où le terrain contredit les guides.

## 1. Les BPM doivent matcher avant tout

- Le disque qui entre est calé **au casque** sur celui qui joue : même tempo, temps alignés.
  Sur une Technics le pitch va de ±8 % ; **le DJ prévoit des platines à ±10 ou ±20 %**, et
  son set monte au fil de la soirée : de 85 BPM à 95, puis 98 si affinité. Deux disques
  dont les tempos diffèrent de plus que la course du pitch ne se mixent pas ; on coupe.
- Le calage se fait au **fader de pitch** pour la vitesse et **à la main sur le plateau**
  (pousser pour rattraper, freiner pour ralentir) pour la phase. Le calage continue
  pendant tout le passage : un vinyle dérive, on le rattrape.
- Le disque qui entre est **lâché sur un temps fort** du disque qui joue (slip-cue), jamais
  au milieu d'un temps.

**Pour le projet :** le fondu simulé prend deux disques à moins de 20 % l'un de l'autre,
rééchantillonne le second au tempo du premier, et aligne ses temps. Dead Internet Theory
(90,9) → Glyph Chamber (73,5) fait 23 % : même à ±20 %, ce n'est pas un passage, c'est une
coupure. Et la préférence de tempo amorcée par la fiche (`LargeurAmorcee` = 0,12 octave,
±8,7 %) a été taillée pour une Technics : à +15 % de pitch elle pèse contre la mesure. La grille vérité de `outils/verite_terrain.py` (période du crate,
phase par l'énergie) donne les temps forts des deux disques pour les aligner.

## 2. Tout se passe entre le temps 4 et le temps 1

- La musique se construit en **phrases** : 4 temps par mesure, et des phrases de 4, 8, 16
  ou 32 mesures. En house, la phrase courante fait 8 mesures (32 temps) ; en hip-hop, on
  compte souvent en 4 mesures (16 temps).
- Le disque qui entre est **lâché au début d'une phrase** du disque qui joue, sur son
  « 1 ». Une fois les deux calés, leurs phrases coïncident et restent alignées.
- **Chaque geste tombe sur un 1 de phrase** : monter un fader, couper une basse, ouvrir
  une basse. Jamais au milieu d'une mesure. Le geste se prépare sur le temps 4 et tombe
  sur le 1 suivant. Le DJ : « souvent le changement se fait entre le temps 4 et le 1 ».

**Pour le projet :** la simulation compte en mesures du disque A, et chaque mouvement de
fader ou d'EQ commence sur un 1 de phrase (multiple de 4 ou 8 mesures). Le rendu, lui,
peut s'attendre à ce que les changements de scène arrivent sur les 1 : c'est un indice de
plus pour ne pas sauter au hasard.

## 3. Les basses ne se superposent jamais : le bass swap

- Deux lignes de basse en même temps, ça ne marche presque jamais. Le disque qui entre
  arrive **basses coupées** (le bouton LOW de son canal à fond en bas) pendant que celui
  qui joue garde les siennes.
- Au moment choisi — un 1 de phrase — on **échange les basses** : LOW de A descend, LOW de
  B monte. Deux écoles : **d'un coup**, « en un seul geste propre » sur le 1 ; ou
  **progressivement sur 8 à 16 mesures**, l'un descend pendant que l'autre monte. Le DJ
  décrit la seconde : « je baisse les basses de master A pour progressivement monter les
  basses de master B ».
- Les médiums et les aigus se mêlent plus facilement ; on les dose au fader de canal, ou
  au filtre (passe-bas sur celui qui sort, passe-haut sur celui qui entre).
- « Less is more » : les coupes extrêmes s'entendent. Le LOW d'une table coupe en plateau
  sous 100–300 Hz selon le modèle (Xone:92 : quatre bandes, LOW vers 100 Hz, isolateur
  jusqu'à −∞).

**Pour le projet :** pendant l'échange, la case « basse » de A doit descendre avec le
bouton et celle de B monter, pendant que les autres cases de A restent. Et l'estimation
du fader (`BlendEstimator`) ne doit pas prendre une coupure de basse pour un départ de A.

## 4. Le passage dure longtemps, les deux disques vivent ensemble

- Le **long blend** : 16 à 32 mesures pendant lesquelles les deux disques jouent, l'entrant
  monte pendant que le sortant descend. À 87 BPM, 16 mesures font 44 secondes ; 32 en font
  88. Le crossfade linéaire de 20 s du fondu actuel est trop court et trop simple.
- Séquence typique d'un passage en douceur, en mesures du disque A :

  | mesures de A | geste |
  |---|---|
  | 1–16 | A seul |
  | 17 (sur le 1) | B lâché sur le 1, basses coupées, fader de canal B monte sur 8 mesures |
  | 25–40 | les deux à égalité, B sans basses |
  | 41 (sur le 1) → 48 | **bass swap** : LOW A descend, LOW B monte, sur 8 mesures (ou d'un coup sur le 1) |
  | 49–56 | les deux, basses chez B |
  | 57 (sur le 1) → 64 | fader de canal A descend sur 8 mesures |
  | 65– | B seul |

- Autres figures, à connaître mais pas à simuler d'abord : la coupure sèche sur un 1 ; le
  filtre balayé ; l'écho de sortie ; le passage sous un break.

**Pour le projet :** c'est la séquence que `relais.py` doit fabriquer, avec des cues qui
ont calé le disque depuis deux minutes (un DJ cale au casque bien plus de 40 s). Le
retrait du portrait de A ne doit arriver qu'à la fin de la mesure 64, pas quand ses
basses partent à la mesure 41.

## 5. Ce que le rendu peut en déduire

- Les scènes s'ouvrent, s'échangent et se ferment **sur des 1 de phrase**.
- Pendant l'échange des basses, la boule du boom passe de la scène A à la scène B **case
  par case**, pas scène entière.
- Le tempo est unique pendant tout le passage : deux grilles, une période.

## 6. Ce que e-e couvre, et ce qui manque (audit du 17 septembre)

| technique | état | où ça se joue |
|---|---|---|
| beatmatch au pitch ±8 % | couvert | préférence amorcée par la fiche, 0,12 octave = la course d'une Technics ; 99 % de justesse sur l'album |
| pitch ±10 / ±20 %, set qui monte de 85 à 98 | **manque** | la largeur de 0,12 octave pèse contre une mesure à +15 % ; le recentrage (`Preferer`) ne se fait qu'à l'amorce et au verrou. Il faut une largeur tirée de la course de la platine, et un recentrage qui suit le pitch poussé |
| rattrapage à la main pendant le passage | couvert | tempo continu, grille qui accumule sa phase, « le relais amorce, il ne verrouille pas » |
| cue au casque, master en salle | couvert | `RoleAnalyseur` Cue / Master, seconde entrée `Signal__CueDevice` |
| lâcher sur le 1 d'une phrase | partiel | le « 1 » du master est voté (60 % de verrouillage) ; les motifs de B arrivent avec le portrait mais supposent que le cue et le master s'accordent sur le 1 — sinon les gestes de B partent d'une demi-mesure à côté. À faire : recaler les motifs de B sur les premières mesures après l'accueil |
| deux disques ensemble 16 à 32 mesures | couvert | suivi joint des deux portraits, huit cases signées, retrait à 90 % seulement |
| bass swap, EQ | partiel | chaque case suit le signal réel, donc la basse de A descend avec le bouton ; **mais** le reste partagé ne sait pas quel boom est à qui, et c'est le geste central du swap : la boule doit passer de la scène A à la scène B. Et `BlendEstimator` sous EQ n'est pas mesuré |
| filtre balayé (Xone:92) | couvert | brillance, ouverture, densité mesurés avant séparation ; dominance par case |
| coupure sèche sur un 1 | partiel | le relais fait accueil, tempo et retrait d'un coup ; l'ouverture lissée des scènes absorbe le saut. Jamais testé |
| mix harmonique (Camelot du crate) | **manque** | à +15 % de pitch la tonalité monte de 2,4 demi-tons : la gamme de la fiche doit être transposée du pitch mesuré (partie entière en demi-tons, reste en accordage) |
| scratch, backspin, spinback, écho de sortie | non couvert, voulu | des événements hors tempo ; le moteur jette plutôt que de suivre. À traiter plus tard comme des ruptures |
| trois platines, set entier | couvert | identité de platine 1 / 2 en alternance ; le tempo de chaque passage recentre la préférence |

**Ordre proposé :** d'abord fabriquer le passage réaliste dans `relais.py` (§4), parce que
c'est l'instrument qui mesure tout le reste ; puis la largeur de préférence tirée de la
course du pitch ; puis le boom par platine pendant le swap ; puis la gamme transposée ;
puis le recalage des motifs de B.

## Sources

- Wikipedia, *Beatmatching* : pitch, nudging, casque, « lower frequencies taken out of one
  of the songs and boosted in the other ».
- Wikipedia, *Phrasing (DJ)* : phrases de 16 mesures, lâcher à une frontière de phrase.
- London Sound Academy, *How to mix with vinyl* : pitch jusqu'à 8 %, lâcher sur « le premier
  temps d'une section, 8 mesures en house, 16 temps en hip-hop », couper le LOW de B puis
  échanger.
- Mixgraph, *DJ transition techniques* : long blend 16–32 mesures, « swap bass from
  outgoing to incoming in one clean move », filtre, coupure, écho, break.
- Skilz DJ Academy, *DJ mix with EQ* : bass swap « the classic technique », EQ tournée
  « sur 16 mesures », « less is more ».
- I DJ NOW, *3 key DJ mixing techniques* : fondu sur 4, 8, 16 ou 32 temps, phrases de 16
  ou 32 temps, bass swap à mi-transition.
