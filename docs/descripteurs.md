# Ce qu'on peut extraire d'une musique en temps reel

Inventaire du domaine, confronte a ce que ce projet mesure deja. La question n'est pas
« que peut-on calculer » — la reponse est : beaucoup trop de choses. Elle est :

> **Un descripteur sans forme a l'ecran est du bruit.**

Chaque ligne ci-dessous est donc jugee sur trois criteres : ce qu'elle dit musicalement,
ce qu'elle couterait, et surtout **ce qu'elle donnerait a voir**. Un descripteur qu'on ne
saurait pas dessiner ne merite pas d'etre calcule.

Les couts sont des ordres de grandeur, pas des mesures. Les seules valeurs mesurees dans
ce depot sont celles du README et de `CLAUDE.md`.

---

## Ce qui est deja mesure

| Grandeur | Ou | Ce qu'elle dit | Forme a l'ecran |
|---|---|---|---|
| RMS | `SpectrumAnalyzer` | l'energie | l'orbe central |
| 12 bandes log | `SpectrumAnalyzer` | la repartition | les rayons |
| Flux spectral positif | `OnsetDetector` | ou ca attaque | les declenchements |
| Tempo par vote | `TempoEstimator` | la pulsation | la grille de `BeatClock` |
| Phase du temps | `BeatClock` | ou l'on est dans le temps | la prediction du kick |
| Kick / clap / charley | `Hits` | qui frappe | anneau, losange, traits |
| Notes graves / medium / aigues | `Voices` | qui joue | disque, polygones, triangles |
| Chromagramme, tonalite | `HarmonicAnalyzer` | la couleur harmonique | nombre de cotes, teinte |
| Centroide, rolloff, ouverture | `TimbreTracker` | la brillance, le filtre | contraction, flou, effacement |
| Nouveaute | `NoveltyDetector` | ca a change de section | le balayage |
| Correlation master/cue | `BlendEstimator` | ou en est le fondu | le fondu de motifs |

Onze familles. C'est deja beaucoup — et pourtant il manque des choses structurantes.

---

## Ce qui manque, par ordre d'importance

### 1. La structure metrique : mesure, phrase, drop

**Le plus gros trou du projet.** Le systeme connait le *temps* mais ignore la *mesure* et
la *phrase*. Or un DJ ne pense jamais en temps : il pense en 8, 16, 32 mesures. C'est
l'unite de la musique qu'il joue, celle sur laquelle il cale ses fondus, ses filtres et
ses coupures.

| Descripteur | Ce qu'il dit | Cout | A l'ecran |
|---|---|---|---|
| Downbeat (le « 1 ») | ou commence la mesure | reutilise le flux par bande, un vote de plus | un accent fort tous les 4 temps au lieu d'un battement uniforme |
| Position dans la phrase (1-16) | ou l'on est dans la structure | compteur cale sur le downbeat | **anticiper** : le motif change *sur* le drop, pas apres |
| Build-up | centroide qui monte, graves qui s'effacent, densite qui grimpe | trois grandeurs deja mesurees, une derivee | la scene qui se tend avant d'exploser |
| Drop / break | chute brutale des graves puis retour | seuil sur l'energie basse | le seul moment ou un changement brutal est *juste* |

Le point crucial : un build-up est **predictible**. Ses trois signaux montent ensemble
pendant huit a seize mesures. Un systeme qui les lit sait ce qui arrive avant que ca
arrive — et c'est la seule facon d'avoir un visuel qui ne soit jamais en retard sur un
drop, quelle que soit la latence de la chaine.

### 2. La stereo — perdue des la capture

`PulseAudioSource` capture en `--channels=1`. **La moitie de l'information spatiale est
jetee avant meme d'entrer dans l'analyse.**

| Descripteur | Ce qu'il dit | Cout | A l'ecran |
|---|---|---|---|
| Largeur (correlation L/R) | mono serre ou nappe large | une soustraction | l'axe horizontal, qui n'existe pas aujourd'hui |
| Balance mid/side par bande | ce qui est au centre, ce qui est aux bords | deux FFT au lieu d'une | le kick au centre, les pads sur les cotes |
| Panoramique de l'attaque | d'ou vient la frappe | difference d'enveloppe | une forme qui naît a gauche ou a droite |

Aujourd'hui tout est centre. Sur un mur projete de plusieurs metres, c'est un gachis
visuel autant qu'un gachis d'information. **Le cout est une seconde FFT**, et la
symetrie du visuel actuel n'en demande probablement pas tant : la largeur seule, calculee
dans le domaine temporel, coute presque rien.

### 3. Le ratio harmonique / percussif — deja calcule, jamais expose

`Hpss` separe les deux composantes et le projet n'utilise que la percussive pour la
detection. **Le rapport de leurs energies est gratuit** : il est deja en memoire.

Il dit une chose qu'aucune autre grandeur ne dit — si l'on est dans un passage de
frappes ou dans une nappe tenue. Un break percussif et un pont atmospherique ont la meme
energie, le meme tempo, souvent le meme timbre. Ils n'ont pas le meme ratio.

### 4. La signature d'instrument — MFCC

Aujourd'hui le xylophone est *devine* par son registre : ce qui frappe dans l'aigu est
suppose etre un xylophone. C'est une heuristique, pas une reconnaissance. Deux instruments
du meme registre sont indiscernables.

Les MFCC (coefficients cepstraux sur echelle mel) donnent la forme de l'enveloppe
spectrale, independamment de la hauteur jouee. C'est la signature timbrale : elle permet
de dire « ces deux notes viennent du meme instrument » — et donc de **regrouper les formes
par source reelle** plutot que par tranche de frequence.

Cout : un banc de filtres mel plus une DCT sur la FFT deja payee. Reel mais modere.
C'est le seul moyen honnete de tenir la promesse « reconnaitre le xylophone ».

### 5. La tension harmonique — le mot « emotion » du titre

| Descripteur | Ce qu'il dit |
|---|---|
| Dissonance sensorielle (Plomp & Levelt) | la rugosite entre partiels proches, la tension physique |
| Distance au centre tonal | de combien on s'eloigne de la tonique |
| Qualite d'accord (majeur / mineur / septieme) | la couleur affective immediate |

Le moteur s'appelle *emotion-calculator* (l'ensemble, *Emotion Emulator*) et mesure aujourd'hui l'energie, le rythme et la
couleur — mais pas la **tension**. Or c'est elle qui fait qu'un morceau serre le ventre.
Le chromagramme est deja la ; en tirer la qualite d'accord est un produit scalaire contre
vingt-quatre gabarits, ce qui est negligeable.

### 6. Les presque gratuits

Un calcul chacun, sur des donnees deja en memoire. A prendre surtout pour le diagnostic.

| Descripteur | Ce qu'il dit |
|---|---|
| Facteur de crete (pic / RMS) | la dynamique — compresse et plat, ou aere et vivant |
| Taux de passages par zero | bruite ou tonal, distingue une caisse claire d'une note |
| Platitude spectrale | deja calcule en interne, jamais expose |
| Etalement, asymetrie du spectre | la largeur du timbre autour du centroide |
| Temps de montee de l'enveloppe | un pluck ou une nappe |
| Sonie ponderee (K-weighting) | le volume *percu*, que le RMS brut ne donne pas |

### 7. Ce qui ne vaut pas le coup ici

Par honnetete, et parce qu'un inventaire qui ne dit jamais non ne sert a rien :

- **F0 monophonique (YIN, pYIN)** — excellent sur une voix seule, illusoire sur un mix
  dense. Le chromagramme rend deja le service utile.
- **Separation de sources par reseau (Demucs, Spleeter)** — la qualite est reelle, la
  latence l'est aussi. Incompatible avec un budget de 43 ms.
- **Reconnaissance de genre, d'humeur, d'instrument par modele** — le crate contient deja
  ces informations, saisies a la main et justes. Les redetecter serait remplacer une
  donnee sure par une donnee approchee.
- **Tempogramme complet** — le vote sur les ecarts fait deja le travail pour ce
  repertoire, a 3,9 % pres.

---

## Ce qu'on fait

Par ordre : ~~structure metrique~~ **faite**, puis **stereo**, puis **ratio
harmonique/percussif**.

La structure metrique a livre plus que prevu. Elle a surtout mis au jour un defaut du
tempo qu'aucun test synthetique n'aurait montre : le repli d'octave n'avait pas de point
fixe, et le repertoire etant joue en swing, le detecteur publiait 133 BPM pour un morceau
a 89. Un descripteur de plus longue portee sert donc aussi de controle sur ceux qui le
precedent — mesure a la sonde, tempo 133 puis 88,9, intervalle de mesure a 0,4 % du
tempo detecte.

Les deux premiers parce qu'ils ouvrent une dimension entiere qui manque — le temps long
pour l'un, l'espace pour l'autre. Le troisieme parce qu'il est deja calcule et qu'il
serait absurde de le jeter.

Les MFCC et la tension harmonique viennent ensuite, quand chacune aura une forme
attribuee. **Pas avant** : un descripteur qu'on calcule sans savoir le dessiner alourdit
le chemin chaud et n'ajoute rien a l'ecran.
