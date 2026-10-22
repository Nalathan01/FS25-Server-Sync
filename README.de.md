![FS25 Server Sync](docs/screenshots/banner_960x260_en.png)

# FS25 Server Sync

**Mit genau den richtigen Mods beitreten.**

FS25 Server Sync ist ein kostenloses Windows-Tool für den Landwirtschafts-Simulator 25. Es liest die Mod-Liste eines Dedicated Servers, vergleicht jede Mod mit deiner eigenen Mod-Sammlung und baut einen eigenen Mods-Ordner, der exakt dem Server entspricht. Mods, die du schon hast, werden verlinkt, fehlende oder abweichende direkt vom Server geladen. Ein Klick stellt das Spiel auf diesen Ordner um, und du kannst beitreten.

Verglichen wird mit demselben Hash, den auch das Spiel verwendet. Zwei Zips mit gleichem Namen, gleicher Version und gleicher Größe können für das Spiel trotzdem verschiedene Dateien sein, zum Beispiel nach erneutem Packen. Solche Fälle werden erkannt und behoben, damit dich das Spiel nicht abweist.

**Download:** [nalathan.itch.io/fs25-server-sync](https://nalathan.itch.io/fs25-server-sync)

English version: [README.md](README.md)

## Funktionen

* **Geführt in vier Schritten:** Prüfen, Abgleichen, Aktivieren, Beitreten. Es gibt immer genau einen nächsten Knopf.
* **Exakt per Spiel-Hash:** Jede Mod wird so verglichen, wie das Spiel sie vergleicht. Identische Dateien bleiben verlinkt, alles andere wird durch die Datei vom Server ersetzt.
* **Nur ein Komplett-Download pro Server:** Der erste Abgleich lädt jede Mod einmal und merkt sich das Ergebnis. Danach wird nur noch geladen, was sich auf dem Server ändert.
* **Nichts liegt doppelt:** Dateien, die du schon hast, werden beim Laden verglichen und nie auf die Platte geschrieben. Deine eigene Mod-Sammlung bleibt unverändert.
* **Eigener Ordner pro Server:** Mods werden per Hardlink eingebunden und belegen keinen zusätzlichen Platz. Zwei Versionen derselben Mod landen nie im selben Ordner.
* **Aktivieren mit einem Klick:** Das Spiel wird auf den Server-Ordner umgestellt, der vorherige Ordner lässt sich jederzeit wiederherstellen. Vorher wird ein Backup der Spieleinstellungen angelegt.
* **Laufende Karte:** Mit Login für das Webinterface erkennt das Tool, welcher Spielstand und welche Karte laufen, und lässt Karten weg, die nur eingeschaltet sind.
* **DLC-Prüfung:** Nutzt der Server ein DLC, das dir fehlt, erfährst du es vor dem Beitreten.
* **Login für das Webinterface:** für Server, deren Mods-Seite eine Anmeldung verlangt. Das Passwort wird verschlüsselt für dein Windows-Konto gespeichert.
* **Für Admins:** Mit Mod-Rechten werden auf dem Server ausgeschaltete Mods als Aufräum-Vorschlag angezeigt. Am Server ändert das Tool nie etwas.
* **Deine eigenen Dateien sind sicher:** Vom Server geladene Dateien werden entfernt, sobald der Server sie nicht mehr nutzt. Eigene Dateien, die vorher in einem Zielordner lagen, kommen in ein Archiv, das du selbst leeren kannst.
* **Server mit Mitspielern teilen:** Einen Server-Eintrag als kleine Datei exportieren und weitergeben. Deine Mitspieler ziehen sie ins Fenster, und alles ist ausgefüllt. Login, Ordner und Passwörter werden nicht geteilt.
* **Einstellungen an einem Ort:** Mod-Bibliothek, Sprache, Archiv und wie viel Platz jeder Server-Ordner wirklich belegt.
* **Aktiven Mods-Ordner direkt umstellen:** zurück auf den Standard-Ordner des Spiels, auf einen Server-Ordner oder auf einen Ordner deiner Wahl.
* **Merkt Änderungen:** Ändert sich bei offenem Tool etwas in einem Server-Ordner, prüft es von selbst neu.
* **Updates mit einem Klick:** Gibt es eine neue Version, zeigt das Tool sie an und aktualisiert sich nach einem Klick selbst. Server und Einstellungen bleiben erhalten.
* **Einzelne Mods neu prüfen:** Zeigt das Spiel bei einer Mod noch ein rotes Warndreieck, hakst du sie in der Liste an, und nur diese wird neu verglichen.
* **Deutsch, Englisch und Französisch,** passend zur Windows-Sprache oder in den Einstellungen gewählt.

![Übersicht](docs/screenshots/1_overview_en.png)
![Erster Abgleich](docs/screenshots/2_first_sync_en.png)

## So geht's

1. `FS25 Server Sync.exe` starten.
2. Deine Mod-Bibliothek wählen, also den Ordner mit deinen Mod-Dateien.
3. Auf "Server hinzufügen" klicken und die Adresse der Mods-Seite des Servers einfügen, zum Beispiel `http://123.45.67.89:8080/mods.html`. Falls die Seite eine Anmeldung verlangt, den Login für das Webinterface eintragen.
4. Auf "Jetzt abgleichen" klicken. Der erste Abgleich mit einem Server lädt jede Mod einmal, je nach Internetgeschwindigkeit dauert das eine Weile.
5. Das Spiel schließen, falls es läuft, und auf "Ordner aktivieren" klicken.
6. Auf "Spiel starten" klicken und dem Server beitreten.

## Voraussetzungen

* Windows 10 oder 11
* Microsoft Edge (unter Windows vorinstalliert)
* .NET Framework 4.8 (unter Windows 10 und 11 vorinstalliert)
* Die Mods-Seite des Servers muss erreichbar sein, öffentlich oder mit Login für das Webinterface.

Keine Installation nötig, es ist eine einzelne exe.

## Selbst bauen

Alles Nötige ist schon Teil von Windows, kein SDK erforderlich:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Die exe landet in `dist\FS25 Server Sync.exe`.

## Gut zu wissen

* Hardlinks funktionieren nur auf einem Laufwerk. Liegt der Server-Ordner auf einem anderen Laufwerk als deine Mod-Bibliothek, werden die Mods stattdessen kopiert und belegen zusätzlich Platz.
* Verlinkte Mods zeigt der Explorer in beiden Ordnern mit voller Größe an. Sie belegen den Platz trotzdem nur einmal. Im Einstellungsfenster siehst du, wie viel jeder Server-Ordner wirklich belegt.
* Neue Downloads landen im Unterordner `_ServerDownloads` deiner Mod-Bibliothek, archivierte Dateien in `_ServerDownloads\_Archiv`.
* Mods, für die der Server keinen Download anbietet, musst du selbst in deine Bibliothek legen.
* Ohne den LS25 Mod Manager schlägt das Tool deinen normalen Mods-Ordner als Bibliothek vor und speichert seine Einstellungen in `%LOCALAPPDATA%\FS25 Server Sync`. Mit dem LS25 Mod Manager liegen sie im Unterordner `FS25 Server Sync` neben der exe.
* Als neues, unsigniertes Programm können Windows SmartScreen und einzelne KI-basierte Virenscanner warnen.

## Datenschutz

Das Tool verbindet sich nur mit den Servern, die du einträgst, und einmal pro Start mit GitHub, um nach einer neuen Version zu schauen. Es gibt kein Konto und kein Tracking. Sein Fenster ist eine kleine Seite, die nur auf deinem eigenen Rechner bereitgestellt wird (127.0.0.1). Ein Passwort für das Webinterface wird mit Windows (DPAPI) für dein Benutzerkonto verschlüsselt und nur an diesen Server geschickt. Das GIANTS-Webinterface nutzt allerdings einfaches http, genau wie beim Login im Browser.

## Feedback

Fragen, Ideen oder ein Server, bei dem etwas nicht klappt? Eröffne ein [Issue](https://github.com/Nalathan01/FS25-Server-Sync/issues) oder schreib einen Kommentar auf der [itch.io-Seite](https://nalathan.itch.io/fs25-server-sync).

## Links

* Download: https://nalathan.itch.io/fs25-server-sync
* Website: https://www.nalathan.de/
* YouTube: https://www.youtube.com/@nalathangaming

## Code-Signatur

Kostenlose Code-Signatur durch [SignPath.io](https://about.signpath.io/), Zertifikat von der [SignPath Foundation](https://signpath.org/).

* Committer und Prüfer: [Nalathan01](https://github.com/Nalathan01)
* Freigabe: [Nalathan01](https://github.com/Nalathan01)

Signiert werden nur Builds, die GitHub Actions automatisch aus dem Quellcode in diesem Repository erstellt.

Datenschutz: Dieses Programm verbindet sich nur mit den Servern, die du selbst einträgst, und mit GitHub, um nach Updates zu schauen. Andere Informationen schickt es nirgendwohin.

## Lizenz

MIT, siehe [LICENSE](LICENSE).

## Hinweis

FS25 Server Sync ist ein inoffizielles Fan-Tool. Es steht in keiner Verbindung zu GIANTS Software und wird von GIANTS Software nicht unterstützt. Farming Simulator ist eine Marke von GIANTS Software. Alle Mod-Namen und Icons gehören ihren Autoren.
