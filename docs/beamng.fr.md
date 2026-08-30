# Export BeamNG.drive

Deux façons d'obtenir un niveau BeamNG avec ce fork. *(English: [beamng.md](beamng.md).)*

| | **Génération directe** | **Export depuis un monde Arma** |
|---|---|---|
| Où | Éditeur de config → *Générer un niveau BeamNG.drive* | Éditeur de monde → *Export → niveau BeamNG.drive* |
| Source | Données réelles (OSM, altimétrie, swisstopo) | Un `.wrp` existant |
| Bâtiments | Volumes swissBUILDINGS3D, texturés | Les modèles Arma de la carte |
| Pour | Construire une région de zéro | Convertir une carte que tu as déjà |

Les deux écrivent un zip de mod. Dépose-le dans `Documents\BeamNG.drive\<version>\mods\`, le niveau apparaît dans Freeroam.

## Génération directe

Règle la carte comme d'habitude — centre, taille de grille, taille de cellule — puis **Générer un niveau BeamNG.drive**. Le style de carte Arma et son avertissement de mod manquant ne s'appliquent pas ici : ce chemin ne touche à aucun asset Arma.

Coche **Altimétrie haute résolution Swisstopo** pour une carte suisse. Cet unique interrupteur déclenche tout ce qui suit.

### Ce que Swisstopo apporte

* **Terrain** — swissALTI3D, le relief réel.
* **Bâtiments** — volumes swissBUILDINGS3D avec leurs vraies toitures, un objet éditable par zone. Ils n'ont aucune texture propre : murs et toits sont donc distingués par l'inclinaison de chaque face et dépliés en mètres — une tuile de façade couvre trois mètres de mur, soit un étage, donc une fenêtre fait la même taille sur un hangar et sur une chaumière. Les fenêtres elles-mêmes sont inventées : aucune donnée ouverte suisse ne dit ce qu'il y a sur un mur.
* **Arbres** — swissSURFACE3D moins le terrain donne la canopée, et chaque sommet local est un arbre qui existe vraiment, à la hauteur qu'il a vraiment. L'espèce vient de cette hauteur : les conifères tiennent la canopée haute, les feuillus le milieu, les buissons le bas. Les couronnes posées sur un toit ou au-dessus d'une chaussée sont supprimées.
* **Sol** — SWISSIMAGE au lieu de Sentinel-2. Zoom 16 et pas plus fin : le niveau porte son sol sur une texture de 4096 pixels, donc tout ce qui est plus net serait téléchargé pour être jeté au redimensionnement.

Le premier passage télécharge beaucoup et le met en cache : environ 1 Mo par kilomètre carré pour l'altimétrie, 19 Mo pour le modèle de surface. Une carte de 16 km, c'est de l'ordre de 12 Go une seule fois.

### Vrais maillages Arma

Arbres, roches et ponts sont dessinés avec des modèles Arma convertis par le portage (**Navigateur d'assets → Construire la bibliothèque de modèles**), partagée par toutes les cartes. Ce qui s'y trouve est utilisé ; ce qui manque retombe sur un panneau généré, donc l'export n'échoue jamais faute de modèle.

## Dimensionnement

La plus grande carte de BeamNG, Italy, fait 4,1 km de côté. Cet export plafonne à 8192 cellules, soit 16,4 km à 2 m — cent fois la surface d'Italy. Ça marche, mais le nombre d'objets, le poids du zip et le téléchargement suivent.

| Grille | Cellule | Carte | Remarque |
|---|---|---|---|
| 4096 | 2 m | 8,2 km | Confortable, proche des usages du jeu |
| 8192 | 2 m | 16,4 km | Un petit district entier, lourd |
| 8192 | 3 m | 24,6 km | Grand district, le terrain perd en finesse |

## Contrôler un export sans lancer le jeu

`tools/check-beamng-export.py` lit le zip et rend un rapport. Chaque contrôle existe parce qu'un vrai défaut est passé devant.

```bash
python tools/check-beamng-export.py <nom de carte> --dll GameRealisticMap.Studio/bin/Debug/net8.0-windows/GameRealisticMap.Studio.dll
```

Le nom suffit — `malden`, `romont` — il est cherché dans le dossier des mods. `--dll` ajoute le contrôle de fraîcheur, qui rattrape l'erreur la plus fréquente de toutes : tester un export antérieur au build qu'on croyait tester.

Sont couverts : normales inversées, géométrie sans texture, marches sur le profil des routes, textures référencées absentes, trous et déchirures aux carrefours, routes repliées, hauteur et brèche des ponts, altitude de la forêt, spéculaire des routes, éclairage de nuit et points de spawn. `--absent <nom>` vérifie qu'une famille d'objets a bien disparu.

## Limites connues

* Les façades sont générées, pas réelles. swissBUILDINGS3D ne contient que de la géométrie, et la bêta 3.0 ne couvre qu'un peu moins de la moitié des tuiles en CityGML — le reste est en DWG et en géodatabase Esri.
* Les routes viennent d'OSM. Là où OSM continue une route en chemin ou en sentier, l'export s'arrête : seules les chaussées d'au moins 3 m sont carrossables.
* Des façades photogrammétriques demanderaient une source avec des vues obliques. Swisstopo n'en publie pas, et les tuiles photoréalistes de Google interdisent d'en dériver un jeu de données.
