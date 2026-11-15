# 🚀 Google Drive Connector for Autodesk Revit

[![Autodesk Revit](https://img.shields.io/badge/Revit-2025%20%7C%202026%20%7C%202027-blue.svg)](https://www.autodesk.com/products/revit/overview)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6.svg)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-10.0%20WPF-512BD4.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Google Drive](https://img.shields.io/badge/Cloud-Google%20Drive%20v3-4285F4.svg)](https://www.google.com/drive/)

**Google Drive Connector** è una soluzione completa di **Worksharing e Collaborazione Cloud per Autodesk Revit (versioni 2025, 2026 e 2027)** basata su Google Drive. 

Permette a più architetti e ingegneri di lavorare contemporaneamente sullo stesso Modello Centrale BIM da postazioni remote, prevenendo corruzioni dei file e senza richiedere le costose licenze di *Autodesk BIM Collaborate Pro*.

---

## 📥 Download Immediato
Scarica la versione più recente direttamente dalla sezione [Releases](https://github.com/FilippoAldrigo/revit-drive-connector/releases):

* **[Scarica RevitDriveConnector_Setup.exe](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Setup.exe)** (Installer rapido in 1 clic per Windows)
* **[Scarica RevitDriveConnector_Installatore.zip](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_Installatore.zip)** (Archivio portabile)
* **[Scarica Codice Sorgente .ZIP](https://github.com/FilippoAldrigo/revit-drive-connector/releases/latest/download/RevitDriveConnector_SourceCode.zip)** (Progetto Visual Studio / C# completo)

---

## 🌟 Funzionalità Principali

### 1. 🛡️ Prevenzione Corruzioni & Lock Esclusivo (Gatekeeper Anti-Desync)
Nei normali cloud storage, sincronizzazioni simultanee corrompono irreversibilmente il file centrale (.rvt). 
Il plugin introduce un **motore di mutua esclusione**:
* Prima di qualsiasi sincronizzazione (STC), acquisisce un **Lock Esclusivo** distribuito.
* Il Gatekeeper verifica l'allineamento degli hash SHA-256 e attende che i delta cloud siano completati sul disco prima di consentire a Revit di salvare.
* Nessun conflitto di versione o sovrascrittura accidentale.

### 2. ⚡ Cache Locale su SSD & Prestazioni Elevate
Revit non lavora mai direttamente sui percorsi cloud lenti. I modelli locali risiedono in una cache SSD veloce, garantendo apertura, salvataggio e navigazione 3D fulminee.

### 3. 👥 Gestione Collaboratori Diretta via Email
Dall'interno di Revit puoi invitare qualsiasi collega inserendo semplicemente il suo indirizzo email Google:
* Il plugin assegna i permessi della cartella tramite le **API Google Drive v3**.
* Il collega apre Revit e trova automaticamente il progetto tra i "Condivisi con me" in "Apri da Google Drive".

### 4. 🔔 Centro Prestiti in Tempo Reale (Worksharing Monitor Cloud)
Possibilità di richiedere e concedere elementi BIM con notifiche popup Live Toast istantanee al proprietario, senza dover sincronizzare l'intero file.

### 5. 🔗 Auto-Healing dei Collegamenti Esterni (CAD, IFC, RVT)
Consolida automaticamente i link esterni nella sottocartella Links con percorsi relativi conformi, eliminando l'errore "Collegamento non trovato".

---

## 🚀 Guida Rapida all'Installazione

1. Scarica ed esegui `RevitDriveConnector_Setup.exe`.
2. L'installer rileva automaticamente le versioni di Autodesk Revit presenti (2025, 2026, 2027) e installa i componenti.
3. Apri **Revit**, accedi al tuo account Google dalla scheda **Drive Connector** ➔ **Account Google**.
4. Sei pronto a collaborare!

---

## 📄 Privacy & Licenza

* **Privacy Policy:** Consulta il file [PRIVACY.md](PRIVACY.md).
* **Licenza:** Rilasciato sotto licenza libera [MIT License](LICENSE).
