# SaveGameCloud

Launcher pour vieux jeux PC qui synchronise leurs sauvegardes dans **Google Drive**.

1. Vous ajoutez un jeu en choisissant son **exécutable**, puis les **fichiers et dossiers** de sauvegarde à synchroniser.
2. Avant chaque lancement, SaveGameCloud compare la sauvegarde locale à celle de Google Drive :
   - **cloud plus récent** → téléchargement automatique, sans question ;
   - **local plus récent** (ou modifié des deux côtés) → une fenêtre vous demande quelle version utiliser ;
   - identique → lancement direct.
3. À la fermeture du jeu, les fichiers sont envoyés dans Google Drive.

Dans Drive, l'arborescence est la suivante : `Mon Drive / SaveGameCloud / <Nom du jeu> / …`.

**[⬇ Télécharger SaveGameCloud.exe](https://github.com/Er4m3d/savegamecloud/releases/latest/download/SaveGameCloud.exe)**
(Windows 10/11 64 bits, fichier unique sans installation) · [Site](https://er4m3d.github.io/savegamecloud/) ·
[Confidentialité](https://er4m3d.github.io/savegamecloud/privacy.html)

> L'exe n'est pas signé. Si Windows affiche « Windows a protégé votre ordinateur », cliquez sur
> *Informations complémentaires*, puis sur *Exécuter quand même*.

---

## 1. Connexion (utilisateurs)

L'identifiant Google de l'application est intégré à `SaveGameCloud.exe`. L'utilisateur clique sur
**Se connecter avec Google**, choisit son compte dans le navigateur et autorise l'accès. Il n'a rien d'autre à configurer.
Ses sauvegardes vont dans **son** Google Drive.

## 2. Configuration Google (développeur, une seule fois)

Google impose que chaque application de bureau ait son propre identifiant OAuth. Comptez environ 5 minutes.
C'est déjà fait pour l'exe officiel. Le fichier `client_secret.json` n'est **pas** dans ce dépôt : pour compiler
votre propre version, créez votre propre identifiant en suivant ces étapes.

1. Ouvrez <https://console.cloud.google.com/> et créez un projet (par ex. « SaveGameCloud »).
2. **API et services → Bibliothèque** : recherchez **Google Drive API**, puis cliquez sur **Activer**.
3. **Google Auth Platform → Branding** (ou « Écran de consentement OAuth ») :
   - type d'utilisateur : **Externe** ;
   - nom de l'application : SaveGameCloud, avec votre adresse e-mail en contact ;
   - **Audience** : ajoutez votre adresse Gmail dans **Utilisateurs test**.
   - *Conseil :* une fois que tout marche, passez l'application **en production** (onglet Audience → « Publier l'application »).
     En mode « Test », Google oblige à se reconnecter tous les 7 jours. La portée utilisée (`drive.file`) ne demande pas
     de validation par Google ; vous verrez seulement un avertissement « application non validée » à la connexion.
4. **Clients → Créer un client** : type **Application de bureau**. Téléchargez le fichier JSON.
5. Enregistrez ce fichier sous `src/SaveGameCloud.Core/client_secret.json`, puis recompilez : il est intégré à l'exe.
   Le fichier est exclu de git par `.gitignore`. Sans ce fichier, la compilation affiche un avertissement et l'appli
   propose d'importer un identifiant.
6. Pour distribuer l'appli, passez-la **en production** (Audience → « Publier l'application »). En mode test, seuls
   les utilisateurs test peuvent se connecter (100 au maximum), et ils doivent se reconnecter tous les 7 jours.

Pour remplacer l'identifiant intégré sans recompiler, posez un `client_secret.json` à côté de `SaveGameCloud.exe`
ou importez-le depuis l'appli : un fichier présent est toujours prioritaire.

> L'identifiant d'une application de bureau n'est pas un vrai secret : Google prévoit qu'il soit livré dans l'exe.
> Si quelqu'un en abuse, créez-en un nouveau dans la console, supprimez l'ancien et publiez une nouvelle version.

> La portée `drive.file` ne donne accès **qu'aux fichiers créés par SaveGameCloud**. L'application ne voit pas
> le reste de votre Drive.

## 3. Utilisation

| Action | Effet |
|---|---|
| **Ajouter un jeu** | Choisissez l'exécutable (`.exe`, `.bat`, `.cmd`, `.lnk`), puis ajoutez les fichiers et dossiers de sauvegarde. |
| **▶ Jouer** (ou double-clic) | Synchronisation, lancement, attente de la fermeture du jeu, puis envoi. |
| **Raccourci bureau** | Crée un raccourci qui lance le jeu *avec* la synchronisation, sans ouvrir la fenêtre principale. |
| **Envoyer vers le cloud / Récupérer depuis le cloud** | Synchronisation manuelle forcée, dans un sens ou dans l'autre. |
| **Sauvegardes de secours** | Ouvre le dossier des copies faites avant chaque téléchargement (les 10 dernières par jeu). |

### Plusieurs PC
Sur chaque PC, ajoutez le jeu avec **exactement le même nom** et les mêmes éléments : le dossier Drive est retrouvé
par son nom. Les chemins sont enregistrés de façon portable : `{GameDir}` (dossier de l'exécutable), `{Documents}`,
`{SavedGames}`, `{AppData}`, `{LocalAppData}`, `{LocalLow}`, `{UserProfile}`, `{ProgramData}`, `{PublicDocuments}`.
Un jeu installé dans `C:\Jeux` sur un PC et dans `D:\Games` sur un autre fonctionne donc sans rien changer.

### Où trouver les sauvegardes d'un vieux jeu ?
Le plus souvent dans le dossier du jeu (`SAVE`, `Saves`, `*.sav`), dans `Documents\My Games\…`, dans
`Saved Games`, dans `%APPDATA%` ou dans `%LOCALAPPDATA%\VirtualStore\…` pour les jeux installés dans
`Program Files`. Le site <https://www.pcgamingwiki.com> indique l'emplacement pour la plupart des jeux.

## 4. Comment les versions sont comparées

- Les fichiers sont comparés par **empreinte MD5**. Si tout est identique, rien n'est transféré.
- Sinon, seules les dates de modification **des fichiers qui diffèrent** sont comparées : la plus récente,
  côté local et côté cloud (marge de 2 s).
  - cloud plus récent → téléchargement automatique ;
  - local plus récent ou dates équivalentes → on vous demande.
- Un téléchargement fait du dossier local un **miroir** du cloud : les fichiers absents du cloud sont supprimés.
  Les fichiers locaux sont d'abord copiés dans les sauvegardes de secours.
- Un envoi fait du cloud un miroir du local. Les fichiers supprimés dans Drive vont dans la **corbeille Drive**,
  donc ils restent récupérables.
- Le jeu est lancé dans un *job* Windows : SaveGameCloud attend la fermeture du jeu **et** de ses processus enfants,
  ce qui gère les jeux qui passent par un petit lanceur.
- Si la vérification n'a pas pu se faire avant la partie (hors ligne, par exemple), l'envoi à la fin de la partie
  vous est demandé au lieu d'être automatique, pour ne jamais écraser une sauvegarde cloud plus récente.

### Garde-fous de taille

La taille n'est pas plafonnée : les fichiers vont dans le Drive de l'utilisateur et l'API Drive ne facture rien.
Une sauvegarde est considérée comme **volumineuse** à partir de **200 Mo ou 2 000 fichiers**. Dans ce cas :

- un avertissement s'affiche quand on ajoute l'élément, et le total est affiché en rouge dans la fenêtre du jeu ;
- avant un envoi, une confirmation est demandée si la sauvegarde n'a jamais été synchronisée à cette taille,
  c'est-à-dire si c'est le premier envoi ou si elle a plus que doublé ;
- si le Google Drive est plein, un message explique quoi faire, et l'appli ne propose pas de réessayer en boucle.

Les seuils se trouvent dans `src/SaveGameCloud.Core/Sync/SizeGuard.cs`.

## 5. Fichiers de l'application

Tout est rangé dans `%APPDATA%\SaveGameCloud` :

| Élément | Rôle |
|---|---|
| `config.json` | Liste des jeux |
| `client_secret.json` | Identifiant OAuth importé (facultatif, remplace celui intégré) |
| `google-token\` | Jeton de connexion Google (supprimé à la déconnexion) |
| `Backups\<jeu>\<date>\` | Copies de secours avant téléchargement |
| `logs\` | Journal, utile en cas de problème |

## 6. Compiler

Prérequis : SDK .NET 10.

```bash
dotnet test
```

```bash
dotnet publish src/SaveGameCloud -c Release -o publish
```

La publication produit un `publish\SaveGameCloud.exe` autonome, qui ne nécessite pas d'installer .NET.

Structure du code :

- `src/SaveGameCloud.Core` : modèles, comparaison et synchronisation (`Sync/SyncEngine.cs`), Google Drive
  (`Cloud/`), lancement et attente du jeu (`Launch/`).
- `src/SaveGameCloud` : interface WPF.
- `tests/SaveGameCloud.Tests` : tests de la synchronisation avec un faux Drive en mémoire, et test du lanceur.

## Limites connues

- Les jeux lancés via Steam, GOG Galaxy ou un autre lanceur qui confie le jeu à un service déjà ouvert ne
  peuvent pas être suivis jusqu'à leur fermeture. Pointez directement vers l'exécutable du jeu.
- Seuls les fichiers envoyés par SaveGameCloud sont visibles par l'application, à cause de la portée `drive.file`.
  Un fichier déposé à la main dans le dossier Drive via le site web sera ignoré.
- Les dossiers vides ne sont pas recréés.
