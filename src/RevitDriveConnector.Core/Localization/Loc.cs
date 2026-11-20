using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitDriveConnector.Core.Localization
{
 public static class Loc
 {
 private static string _currentLanguage = "it";

 public static string CurrentLanguage
 {
 get => _currentLanguage;
 set
 {
 if (value == "en" || value == "it")
 {
 _currentLanguage = value;
 }
 }
 }

 public static void Initialize(string? configLanguage = null, string? revitLanguage = null)
 {
 // 1. Se configurata lingua specifica ("it" o "en"), usala
 if (!string.IsNullOrEmpty(configLanguage) && configLanguage != "Auto")
 {
 CurrentLanguage = configLanguage.ToLowerInvariant();
 return;
 }

 // 2. Se passato codice lingua da Revit (es. "Italian", "English_USA", "ENU", "ITA")
 if (!string.IsNullOrEmpty(revitLanguage))
 {
 string r = revitLanguage.ToLowerInvariant();
 if (r.Contains("ita") || r.Contains("italian"))
 {
 CurrentLanguage = "it";
 return;
 }
 CurrentLanguage = "en";
 return;
 }

 // 3. Fallback: lingua del sistema operativo Windows
 string osLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
 CurrentLanguage = (osLang == "it") ? "it" : "en";
 }

 public static string Tr(string key)
 {
 if (Strings.TryGetValue(key, out var dict))
 {
 if (dict.TryGetValue(_currentLanguage, out var val))
 {
 return val;
 }
 if (dict.TryGetValue("en", out var valEn))
 {
 return valEn;
 }
 }
 return key;
 }

 public static string Format(string key, params object[] args)
 {
 string template = Tr(key);
 try
 {
 return string.Format(template, args);
 }
 catch
 {
 return template;
 }
 }

 private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
 {
 // Ribbon Tab & Panels
 ["Ribbon_TabName"] = new() { ["it"] = "Drive Connector", ["en"] = "Drive Connector" },
 ["Panel_Projects"] = new() { ["it"] = "Progetti Cloud", ["en"] = "Cloud Projects" },
 ["Panel_Sync"] = new() { ["it"] = "Sincronizzazione", ["en"] = "Synchronization" },
 ["Panel_Borrow"] = new() { ["it"] = "Prestiti & Modifiche", ["en"] = "Borrowing & Changes" },
 ["Panel_Links"] = new() { ["it"] = "Collegamenti Cloud", ["en"] = "Cloud Links" },
 ["Panel_Config"] = new() { ["it"] = "Configurazione", ["en"] = "Configuration" },

 // Ribbon Buttons
 ["Btn_Open"] = new() { ["it"] = "Apri da\nGoogle Drive", ["en"] = "Open from\nGoogle Drive" },
 ["Btn_Open_Tip"] = new() { 
 ["it"] = "Apre un modello centrale dal cloud creando automaticamente il modello locale sincronizzato.",
 ["en"] = "Opens a central model from the cloud, automatically creating a synchronized local model."
 },
 ["Btn_Publish"] = new() { ["it"] = "Pubblica\nsu Drive", ["en"] = "Publish\nto Drive" },
 ["Btn_Publish_Tip"] = new() {
 ["it"] = "Converte il progetto corrente in un Modello Centrale condiviso e lo carica su Google Drive.",
 ["en"] = "Converts the current project into a shared Central Model and uploads it to Google Drive."
 },
 ["Btn_Share"] = new() { ["it"] = "Condividi\nProgetto", ["en"] = "Share\nProject" },
 ["Btn_Share_Tip"] = new() {
 ["it"] = "Aggiungi colleghi e collaboratori tramite la loro email Google per consentire loro di aprire e sincronizzare il progetto.",
 ["en"] = "Add teammates via their Google email to allow them to open and synchronize the project."
 },
 ["Btn_Sync"] = new() { ["it"] = "Sincronizza\nDrive STC", ["en"] = "Sync with\nDrive STC" },
 ["Btn_Sync_Tip"] = new() {
 ["it"] = "Acquisisce il lock esclusivo, allinea i delta da Google Drive ed esegue la sincronizzazione protetta.",
 ["en"] = "Acquires exclusive lock, aligns delta files from Google Drive, and executes safe synchronization."
 },
 ["Btn_Status"] = new() { ["it"] = "Stato &\nUtenti Attivi", ["en"] = "Status &\nActive Users" },
 ["Btn_Status_Tip"] = new() {
 ["it"] = "Mostra lo stato della connessione cloud, utenti attivi sul progetto e lock correnti.",
 ["en"] = "Displays cloud connection status, active project users, and current locks."
 },
 ["Btn_RequestBorrow"] = new() { ["it"] = "Chiedi Modifica\nElemento", ["en"] = "Request Element\nBorrow" },
 ["Btn_RequestBorrow_Tip"] = new() {
 ["it"] = "Richiede al proprietario il permesso immediato di modificare l'elemento selezionato.",
 ["en"] = "Requests immediate permission from the element owner to edit the selected element."
 },
 ["Btn_BorrowCenter"] = new() { ["it"] = "Centro\nPrestiti", ["en"] = "Borrowing\nCenter" },
 ["Btn_BorrowCenter_Tip"] = new() {
 ["it"] = "Mostra le richieste di prestito in arrivo dai colleghi e quelle in attesa di approvazione.",
 ["en"] = "Displays incoming borrow requests from teammates and pending requests."
 },
 ["Btn_ManageLinks"] = new() { ["it"] = "Gestione\nCollegamenti", ["en"] = "Manage\nLinks" },
 ["Btn_ManageLinks_Tip"] = new() {
 ["it"] = "Consolida i collegamenti esterni (Revit, DWG, IFC) nella cartella Links di Google Drive e imposta percorsi relativi.",
 ["en"] = "Consolidates external links (Revit, DWG, IFC) into the Google Drive Links folder with relative paths."
 },
 ["Btn_Account"] = new() { ["it"] = "Account\nGoogle", ["en"] = "Google\nAccount" },
 ["Btn_Account_Tip"] = new() {
 ["it"] = "Accedi al tuo account Google per visualizzare e sincronizzare automaticamente i progetti condivisi dai tuoi colleghi.",
 ["en"] = "Sign in with your Google account to discover and synchronize projects shared by your teammates."
 },
 ["Btn_Settings"] = new() { ["it"] = "Impostazioni\nCloud", ["en"] = "Cloud\nSettings" },
 ["Btn_Settings_Tip"] = new() {
 ["it"] = "Configura il percorso della cartella Google Drive / Cloud Storage e i parametri di sincronizzazione.",
 ["en"] = "Configure Google Drive storage folder path and synchronization parameters."
 },

 // Open Project Window
 ["Open_Title"] = new() { ["it"] = "Google Drive Connector - Progetti Cloud", ["en"] = "Google Drive Connector - Cloud Projects" },
 ["Open_Header"] = new() { ["it"] = "Progetti Condivisi su Google Drive", ["en"] = "Projects Shared on Google Drive" },
 ["Open_Subtitle"] = new() {
 ["it"] = "Seleziona un progetto centrale dal cloud. Verra sincronizzato e aperto come modello locale sul tuo computer.",
 ["en"] = "Select a central model from the cloud. It will be synchronized and opened as a local model on your computer."
 },
 ["Open_NoAccount"] = new() { ["it"] = "Account Google non connesso", ["en"] = "Google Account not connected" },
 ["Open_Manage"] = new() { ["it"] = "Gestisci", ["en"] = "Manage" },
 ["Open_ColName"] = new() { ["it"] = "Nome Progetto", ["en"] = "Project Name" },
 ["Open_ColSource"] = new() { ["it"] = "Origine", ["en"] = "Source" },
 ["Open_ColStatus"] = new() { ["it"] = "Stato", ["en"] = "Status" },
 ["Open_ColVersion"] = new() { ["it"] = "Versione", ["en"] = "Version" },
 ["Open_ColModified"] = new() { ["it"] = "Ultima Modifica", ["en"] = "Last Modified" },
 ["Open_Refresh"] = new() { ["it"] = "Aggiorna Elenco", ["en"] = "Refresh List" },
 ["Open_Collaborators"] = new() { ["it"] = "Collaboratori...", ["en"] = "Collaborators..." },
 ["Open_Cancel"] = new() { ["it"] = "Annulla", ["en"] = "Cancel" },
 ["Open_OpenLocal"] = new() { ["it"] = "Apri Modello Locale", ["en"] = "Open Local Model" },
 ["Open_SyncAndOpen"] = new() { ["it"] = "Sincronizza e Apri", ["en"] = "Sync and Open" },
 ["Open_ReadyLocal"] = new() { ["it"] = "Pronto in Locale", ["en"] = "Ready Locally" },
 ["Open_OnCloud"] = new() { ["it"] = "Su Cloud (Condiviso)", ["en"] = "On Cloud (Shared)" },
 ["Open_MyDrive"] = new() { ["it"] = "Il mio Drive", ["en"] = "My Drive" },
 ["Open_SharedBy"] = new() { ["it"] = "Condiviso da", ["en"] = "Shared by" },
 ["Open_Searching"] = new() { ["it"] = "Ricerca progetti su Google Drive...", ["en"] = "Searching projects on Google Drive..." },
 ["Open_FoundCount"] = new() { ["it"] = "Trovati {0} progetti su Google Drive (locali e condivisi via cloud).", ["en"] = "Found {0} projects on Google Drive (local and cloud shared)." },
 ["Open_FoundLocalOnly"] = new() { ["it"] = "Trovati {0} progetti in cache locale. Clicca 'Gestisci' per collegare Google e vedere tutti i progetti condivisi.", ["en"] = "Found {0} locally cached projects. Click 'Manage' to connect Google and see all shared projects." },
 ["Open_ConnectRequired"] = new() { ["it"] = "Account Google non connesso o sessione scaduta: clicca 'Gestisci' in alto a destra per accedere e vedere i progetti condivisi.", ["en"] = "Google Account not connected or session expired: click 'Manage' at top right to sign in and view shared projects." },
 ["Open_ConnectPrompt"] = new() { ["it"] = "La sessione Google Drive non è attiva o è scaduta.\n\nPer visualizzare i progetti condivisi dai collaboratori è necessario accedere con il proprio Account Google.\n\nVuoi accedere con Google adesso?", ["en"] = "Your Google Drive session is not active or has expired.\n\nYou need to sign in with your Google Account to see projects shared by collaborators.\n\nWould you like to sign in with Google now?" },
 ["Open_NoProjects"] = new() { ["it"] = "Nessun progetto trovato su Google Drive. Pubblicane uno con 'Pubblica su Drive'.", ["en"] = "No projects found on Google Drive. Publish one using 'Publish to Drive'." },
 ["Open_Aligning"] = new() { ["it"] = "Download e allineamento di '{0}' in corso...", ["en"] = "Downloading and aligning '{0}'..." },
 ["Open_SyncingCloud"] = new() { ["it"] = "Sincronizzazione progetto condiviso '{0}' da Google Drive...", ["en"] = "Synchronizing shared project '{0}' from Google Drive..." },
 ["Open_CentralNotFound"] = new() { ["it"] = "Errore: File centrale non trovato sul disco locale.", ["en"] = "Error: Central file not found on local disk." },
 ["Open_ChkCreateNewLocal"] = new() { ["it"] = "Crea nuova copia locale aggiornata dal Centrale (Consigliato)", ["en"] = "Create fresh local copy from Central (Recommended)" },
 ["Open_LocalIncompatibleNotice"] = new() {
 ["it"] = "Il modello locale non e allineato con il Centrale su Google Drive.\n\nPer evitare conflitti, clicca su 'Apri da Google Drive' nella scheda Drive Connector per rigenerare una copia locale pulita e aggiornata.",
 ["en"] = "The local model is not aligned with the Central model on Google Drive.\n\nTo prevent conflicts, click 'Open from Google Drive' on the Drive Connector tab to regenerate a fresh local copy."
 },
 ["Open_AutoReloadLatestSuccess"] = new() {
 ["it"] = "Modello locale aggiornato automaticamente con le ultime modifiche del Centrale (Reload Latest).",
 ["en"] = "Local model automatically updated with latest Central changes (Reload Latest)."
 },
 ["Open_LocalAlreadyOpen"] = new() {
 ["it"] = "Il modello locale e gia aperto in Revit. Esecuzione di 'Ricarica Ultime Modifiche' (Reload Latest)...",
 ["en"] = "The local model is already open in Revit. Executing 'Reload Latest'..."
 },

 // Share Project Window
 ["Share_Title"] = new() { ["it"] = "Google Drive Connector - Condividi con Collaboratori", ["en"] = "Google Drive Connector - Share with Collaborators" },
 ["Share_Header"] = new() { ["it"] = "Condivisione Progetto Cloud", ["en"] = "Cloud Project Sharing" },
 ["Share_Subtitle"] = new() {
 ["it"] = "Aggiungi l'indirizzo email Google dei tuoi colleghi. Riceveranno l'accesso su Google Drive e potranno aprire il progetto direttamente da Revit con un clic.",
 ["en"] = "Add the Google email of your teammates. They will receive access on Google Drive and can open the project directly from Revit with one click."
 },
 ["Share_SelectedProject"] = new() { ["it"] = "Progetto selezionato:", ["en"] = "Selected project:" },
 ["Share_AddHeader"] = new() { ["it"] = "Aggiungi un nuovo collaboratore", ["en"] = "Add a new collaborator" },
 ["Share_EmailPlaceholder"] = new() { ["it"] = "collega@gmail.com", ["en"] = "teammate@gmail.com" },
 ["Share_RoleEditor"] = new() { ["it"] = "Editor", ["en"] = "Editor" },
 ["Share_RoleViewer"] = new() { ["it"] = "Visualizzatore", ["en"] = "Viewer" },
 ["Share_BtnAdd"] = new() { ["it"] = "Aggiungi", ["en"] = "Add" },
 ["Share_NotifyCheck"] = new() { ["it"] = "Invia notifica di invito via email da Google", ["en"] = "Send email invitation notification via Google" },
 ["Share_ActiveMembers"] = new() { ["it"] = "Membri del team con accesso a questo progetto:", ["en"] = "Team members with access to this project:" },
 ["Share_ColEmail"] = new() { ["it"] = "Email Google", ["en"] = "Google Email" },
 ["Share_ColName"] = new() { ["it"] = "Nome", ["en"] = "Name" },
 ["Share_ColRole"] = new() { ["it"] = "Permesso", ["en"] = "Permission" },
 ["Share_ColAction"] = new() { ["it"] = "Azione", ["en"] = "Action" },
 ["Share_BtnRemove"] = new() { ["it"] = "Rimuovi", ["en"] = "Remove" },
 ["Share_BtnRefresh"] = new() { ["it"] = "Aggiorna Lista", ["en"] = "Refresh List" },
 ["Share_BtnClose"] = new() { ["it"] = "Chiudi", ["en"] = "Close" },
 ["Share_InvalidEmail"] = new() { ["it"] = "Inserisci un indirizzo email valido.", ["en"] = "Please enter a valid email address." },
 ["Share_Sending"] = new() { ["it"] = "Invio autorizzazione a Google Drive in corso...", ["en"] = "Sending authorization to Google Drive..." },
 ["Share_Success"] = new() { ["it"] = "Accesso concesso a {0}!", ["en"] = "Access granted to {0}!" },
 ["Share_Revoked"] = new() { ["it"] = "Accesso revocato per {0}.", ["en"] = "Access revoked for {0}." },
 ["Share_ConfirmRevokeTitle"] = new() { ["it"] = "Revoca Accesso Collaboratore", ["en"] = "Revoke Collaborator Access" },
 ["Share_ConfirmRevoke"] = new() { 
 ["it"] = "Sei sicuro di voler revocare l'accesso al progetto a:\n{0}?",
 ["en"] = "Are you sure you want to revoke project access for:\n{0}?"
 },

 // Google Account Window
 ["Account_Title"] = new() { ["it"] = "Google Drive Connector - Gestione Account Google", ["en"] = "Google Drive Connector - Google Account Management" },
 ["Account_Header"] = new() { ["it"] = "Account Google & Progetti Condivisi", ["en"] = "Google Account & Shared Projects" },
 ["Account_Subtitle"] = new() {
 ["it"] = "Collega il tuo account Google per visualizzare e aprire direttamente i progetti condivisi con te dai tuoi colleghi.",
 ["en"] = "Connect your Google account to discover and open projects shared with you by your team."
 },
 ["Account_NoConnected"] = new() { ["it"] = "Nessun Account Connesso", ["en"] = "No Account Connected" },
 ["Account_Prompt"] = new() {
 ["it"] = "Accedi con Google per abilitare la sincronizzazione dei progetti condivisi",
 ["en"] = "Sign in with Google to enable shared project synchronization"
 },
 ["Account_NotConnected"] = new() { ["it"] = "Stato: Non Connesso", ["en"] = "Status: Not Connected" },
 ["Account_Connected"] = new() { ["it"] = "Connesso alle API di Google Drive", ["en"] = "Connected to Google Drive API" },
 ["Account_BtnConnect"] = new() { ["it"] = "Accedi con Google", ["en"] = "Sign in with Google" },
 ["Account_BtnDisconnect"] = new() { ["it"] = "Disconnetti", ["en"] = "Sign out" },
 ["Account_CredHeader"] = new() { ["it"] = "Configurazione Credenziali Google (OAuth 2.0)", ["en"] = "Google Credentials Setup (OAuth 2.0)" },
 ["Account_CredDesc"] = new() {
 ["it"] = "Per consentire l'accesso sicuro all'API di Google Drive, e richiesto il file credentials.json generato su Google Cloud Console (Desktop App).",
 ["en"] = "To allow secure access to Google Drive API, credentials.json generated in Google Cloud Console is required."
 },
 ["Account_BtnLoadCred"] = new() { ["it"] = "Seleziona File credentials.json...", ["en"] = "Select credentials.json file..." },
 ["Account_CredConfigured"] = new() { ["it"] = "Credenziali OAuth configurate", ["en"] = "OAuth credentials configured" },
 ["Account_CredMissing"] = new() { ["it"] = "File credentials.json non presente", ["en"] = "credentials.json file missing" },
 ["Account_ManualHeader"] = new() { ["it"] = "Inserisci manualmente Client ID e Client Secret", ["en"] = "Manually enter Client ID and Client Secret" },
 ["Account_BtnSaveManual"] = new() { ["it"] = "Salva Credenziali Manuali", ["en"] = "Save Manual Credentials" },
 ["Account_GuideTitle"] = new() { ["it"] = "Come Condividere i Progetti con i Colleghi", ["en"] = "How to Share Projects with Teammates" },
 ["Account_Guide1"] = new() { ["it"] = "1. Chi pubblica il progetto: basta premere 'Pubblica su Drive' in Revit.", ["en"] = "1. Project publisher: simply click 'Publish to Drive' in Revit." },
 ["Account_Guide2"] = new() { ["it"] = "2. Aggiungi i colleghi: usa il pulsante 'Condividi Progetto' e inserisci la loro email Google.", ["en"] = "2. Add teammates: use 'Share Project' button and enter their Google email." },
 ["Account_Guide3"] = new() { ["it"] = "3. I colleghi: aprendo 'Progetti Cloud' troveranno automaticamente il progetto pronto da aprire.", ["en"] = "3. Teammates: opening 'Cloud Projects' will automatically show the project ready to open." },
 ["Account_CheckingStatus"] = new() { ["it"] = "Verifica stato account Google in corso...", ["en"] = "Checking Google account status..." },
 ["Account_ConfirmDisconnect"] = new() {
 ["it"] = "Vuoi davvero disconnettere il tuo account Google dal plugin?\nNon visualizzerai piu i progetti condivisi via cloud fino al prossimo accesso.",
 ["en"] = "Do you really want to disconnect your Google account from the plugin?\nYou will not see shared cloud projects until next sign in."
 },
 ["Account_DisconnectSuccess"] = new() { ["it"] = "Account Google disconnesso con successo.", ["en"] = "Google account disconnected successfully." },

 // Borrow Center Window
 ["Borrow_Title"] = new() { ["it"] = "Centro Gestione Prestiti & Richieste (Worksharing Monitor)", ["en"] = "Borrow Management & Requests Center (Worksharing Monitor)" },
 ["Borrow_Header"] = new() { ["it"] = "Gestione Prestiti Elementi in Tempo Reale", ["en"] = "Real-Time Element Borrowing Management" },
 ["Borrow_Subtitle"] = new() {
 ["it"] = "Scambia i permessi di modifica sugli oggetti BIM senza dover attendere la sincronizzazione completa.",
 ["en"] = "Exchange editing permissions on BIM objects without waiting for full model synchronization."
 },
 ["Borrow_TabIncoming"] = new() { ["it"] = "Richieste Ricevute (Da approvare)", ["en"] = "Incoming Requests (To Approve)" },
 ["Borrow_TabOutgoing"] = new() { ["it"] = "Mie Richieste Inviate", ["en"] = "My Sent Requests" },
 ["Borrow_ColColleague"] = new() { ["it"] = "Collega", ["en"] = "Teammate" },
 ["Borrow_ColElement"] = new() { ["it"] = "Elemento Richiesto", ["en"] = "Requested Element" },
 ["Borrow_ColMachine"] = new() { ["it"] = "Computer", ["en"] = "Computer" },
 ["Borrow_ColDate"] = new() { ["it"] = "Data", ["en"] = "Date" },
 ["Borrow_ColOwner"] = new() { ["it"] = "Proprietario Attuale", ["en"] = "Current Owner" },
 ["Borrow_ColStatus"] = new() { ["it"] = "Stato Richiesta", ["en"] = "Request Status" },
 ["Borrow_BtnDeny"] = new() { ["it"] = "Rifiuta", ["en"] = "Deny" },
 ["Borrow_BtnGrant"] = new() { ["it"] = "Concedi Prestito", ["en"] = "Grant Borrow" },
 ["Borrow_BtnRefresh"] = new() { ["it"] = "Aggiorna", ["en"] = "Refresh" },
 ["Borrow_BtnCheckout"] = new() { ["it"] = "Prendi in Carico Elementi Approvati", ["en"] = "Checkout Approved Elements" },
 ["Borrow_BtnRelinquish"] = new() { ["it"] = "Rilascia Tutti i Miei Elementi (Relinquish All)", ["en"] = "Relinquish All My Elements" },
 ["Borrow_SelectPrompt"] = new() { ["it"] = "Seleziona una richiesta dall'elenco prima di cliccare.", ["en"] = "Please select a request from the list first." },
 ["Borrow_GrantedSuccess"] = new() { ["it"] = "Prestito concesso con successo a {0}!", ["en"] = "Borrow successfully granted to {0}!" },
 ["Borrow_NoApproved"] = new() { ["it"] = "Nessuna richiesta approvata in sospeso da prendere in carico.", ["en"] = "No pending approved requests to checkout." },
 ["Borrow_CheckoutSuccess"] = new() { ["it"] = "Presi in carico con successo {0} elementi approvati dal collega!", ["en"] = "Successfully checked out {0} approved elements from teammate!" },
 ["Borrow_ConfirmRelinquish"] = new() { ["it"] = "Vuoi rilasciare tutti i tuoi elementi presi in prestito per renderli disponibili ai colleghi?", ["en"] = "Do you want to relinquish all your borrowed elements to make them available to teammates?" },
 ["Borrow_RelinquishSuccess"] = new() { ["it"] = "Tutti gli elementi in prestito sono stati rilasciati con successo!", ["en"] = "All borrowed elements have been successfully relinquished!" },

 // Borrow Toast
 ["Toast_Title"] = new() { ["it"] = "Richiesta di Modifica Elemento", ["en"] = "Element Editing Request" },
 ["Toast_TitleGranted"] = new() { ["it"] = "Prestito Concesso!", ["en"] = "Borrow Granted!" },
 ["Toast_TitleDenied"] = new() { ["it"] = "Richiesta Rifiutata", ["en"] = "Request Denied" },
 ["Toast_Prompt"] = new() { ["it"] = "{0} ti chiede il permesso di modificare:", ["en"] = "{0} requests permission to edit:" },
 ["Toast_PromptGranted"] = new() { ["it"] = "{0} ha concesso il permesso di modifica per:", ["en"] = "{0} granted editing permission for:" },
 ["Toast_PromptDenied"] = new() { ["it"] = "{0} ha rifiutato la richiesta di modifica per:", ["en"] = "{0} denied editing permission for:" },
 ["Toast_Element"] = new() { ["it"] = "Elemento:", ["en"] = "Element:" },
 ["Toast_UniqueId"] = new() { ["it"] = "ID Univoco:", ["en"] = "Unique ID:" },
 ["Toast_Message"] = new() { ["it"] = "Messaggio:", ["en"] = "Message:" },
 ["Toast_BtnGrant"] = new() { ["it"] = "Concedi", ["en"] = "Grant" },
 ["Toast_BtnDeny"] = new() { ["it"] = "Rifiuta", ["en"] = "Deny" },
 ["Toast_BtnClose"] = new() { ["it"] = "Capito / Chiudi", ["en"] = "Understood / Close" },
 ["Toast_GrantedStatus"] = new() { ["it"] = "Prestito concesso a {0}!", ["en"] = "Borrow granted to {0}!" },
 ["Toast_DeniedStatus"] = new() { ["it"] = "Richiesta rifiutata.", ["en"] = "Request denied." },

 // Manage Links Window
 ["Links_Title"] = new() { ["it"] = "Google Drive Connector - Gestione Collegamenti Esterni", ["en"] = "Google Drive Connector - External Links Management" },
 ["Links_Header"] = new() { ["it"] = "Gestione Collegamenti Esterni Cloud", ["en"] = "Cloud External Links Management" },
 ["Links_Subtitle"] = new() {
 ["it"] = "Converti automaticamente tutti i collegamenti (Revit, DWG, IFC) in percorsi relativi all'interno della cartella Links di Google Drive per evitare errori di 'Collegamento non trovato' tra i colleghi del team.",
 ["en"] = "Automatically convert all external links (Revit, DWG, IFC) to relative paths inside the Google Drive Links folder to avoid 'Link Not Found' errors among teammates."
 },
 ["Links_Alert"] = new() {
 ["it"] = "Rilevati collegamenti esterni con percorsi assoluti locali. Clicca su 'Consolida' per condividerli su Drive.",
 ["en"] = "Detected external links with absolute local paths. Click 'Consolidate' to share them on Drive."
 },
 ["Links_ColName"] = new() { ["it"] = "Nome File", ["en"] = "File Name" },
 ["Links_ColType"] = new() { ["it"] = "Tipo", ["en"] = "Type" },
 ["Links_ColPath"] = new() { ["it"] = "Percorso Attuale", ["en"] = "Current Path" },
 ["Links_ColStatus"] = new() { ["it"] = "Stato", ["en"] = "Status" },
 ["Links_BtnRefresh"] = new() { ["it"] = "Aggiorna Elenco", ["en"] = "Refresh List" },
 ["Links_BtnConsolidate"] = new() { ["it"] = "Consolida e Relativizza su Drive", ["en"] = "Consolidate & Relativize on Drive" },
 ["Links_AllCompliant"] = new() { ["it"] = "Tutti i collegamenti ({0}) sono conformi e impostati con percorsi relativi!", ["en"] = "All links ({0}) are compliant and configured with relative paths!" },
 ["Links_NonCompliant"] = new() { ["it"] = "Rilevati {0} collegamenti non condivisi o con percorsi assoluti locali. Clicca su 'Consolida' per trasferirli su Google Drive.", ["en"] = "Detected {0} non-shared links or links with absolute local paths. Click 'Consolidate' to transfer them to Google Drive." },
 ["Links_SuccessMsg"] = new() { ["it"] = "Operazione completata con successo!\n\n{0} collegamenti sono stati copiati nella cartella 'Links' e convertiti in percorsi relativi.", ["en"] = "Operation completed successfully!\n\n{0} links were copied to the 'Links' folder and converted to relative paths." },

 // Settings Window
 ["Settings_Title"] = new() { ["it"] = "Google Drive Connector - Impostazioni", ["en"] = "Google Drive Connector - Settings" },
 ["Settings_Header"] = new() { ["it"] = "Configurazione Drive Connector", ["en"] = "Drive Connector Configuration" },
 ["Settings_Subtitle"] = new() {
 ["it"] = "Configura il percorso dello storage cloud (Google Drive o cartella condivisa) e la lingua dell'interfaccia.",
 ["en"] = "Configure cloud storage folder path (Google Drive or shared folder) and UI language."
 },
 ["Settings_CloudPath"] = new() { ["it"] = "Percorso Storage Cloud (Google Drive / Condiviso):", ["en"] = "Cloud Storage Path (Google Drive / Shared):" },
 ["Settings_CachePath"] = new() { ["it"] = "Percorso Cache Locale (SSD Veloce):", ["en"] = "Local Cache Path (Fast SSD):" },
 ["Settings_CoordUrl"] = new() { ["it"] = "Indirizzo Coordinator Server:", ["en"] = "Coordinator Server Address:" },
 ["Settings_Language"] = new() { ["it"] = "Lingua Interfaccia (Language):", ["en"] = "Interface Language:" },
 ["Settings_BtnBrowse"] = new() { ["it"] = "Sfoglia...", ["en"] = "Browse..." },
 ["Settings_BtnSave"] = new() { ["it"] = "Salva Impostazioni", ["en"] = "Save Settings" },
 ["Settings_SavedMsg"] = new() { ["it"] = "Impostazioni salvate con successo.", ["en"] = "Settings saved successfully." },

 // Sync Gatekeeper Dialog
 ["Gatekeeper_Title"] = new() { ["it"] = "Google Drive Connector - Verifica Integrita", ["en"] = "Google Drive Connector - Integrity Check" },
 ["Gatekeeper_Header"] = new() { ["it"] = "Controllo Integrita & Anti-Desync", ["en"] = "Integrity & Anti-Desync Check" },
 ["Gatekeeper_Subtitle"] = new() { ["it"] = "Verifica allineamento delta con Google Drive...", ["en"] = "Checking delta file alignment with Google Drive..." },
 ["Gatekeeper_Waiting"] = new() { ["it"] = "In attesa del completamento del download...", ["en"] = "Waiting for file download completion..." },
 ["Gatekeeper_Timer"] = new() { ["it"] = "Attesa: {0}s (max {1}s)", ["en"] = "Waiting: {0}s (max {1}s)" },
 ["Gatekeeper_Aligned"] = new() { ["it"] = "Modello allineato ed integro! Avvio sincronizzazione...", ["en"] = "Model aligned and verified! Starting synchronization..." },
 ["Gatekeeper_Timeout"] = new() { ["it"] = "Timeout: Google Drive non ha completato il download in tempo utile.", ["en"] = "Timeout: Google Drive did not complete download in time." },

 // Commands & Common Revit Prompts
 ["Cmd_NoActiveDoc"] = new() { ["it"] = "Nessun progetto aperto in Revit.", ["en"] = "No open project in Revit." },
 ["Cmd_NotWorkshared"] = new() { ["it"] = "Il progetto non e un modello condiviso (Workshared).", ["en"] = "The project is not a workshared model." },
 ["Publish_Title"] = new() { ["it"] = "Pubblica su Google Drive", ["en"] = "Publish to Google Drive" },
 ["Publish_AlreadyPublished"] = new() {
 ["it"] = "Questo modello e gia pubblicato e condiviso su Google Drive!\n\nNon occorre ri-pubblicarlo: per inviare le tue modifiche al cloud e ai tuoi colleghi, clicca semplicemente sul pulsante 'Sincronizza Drive STC'.",
 ["en"] = "This model is already published and shared on Google Drive!\n\nNo need to re-publish: to send your changes to the cloud and teammates, simply click 'Sync with Drive STC'."
 },
 ["Publish_AskPublish"] = new() { ["it"] = "Vuoi pubblicare '{0}' su Google Drive come modello centrale condiviso?", ["en"] = "Do you want to publish '{0}' to Google Drive as a shared central model?" },
 ["Publish_AskPublishContent"] = new() { ["it"] = "Il modello verra configurato per il Worksharing multiutente e caricato sul tuo Google Drive nel cloud.", ["en"] = "The model will be configured for multi-user worksharing and uploaded to Google Drive." },
 ["Publish_ErrUpload"] = new() { ["it"] = "Salvataggio locale riuscito, ma errore durante l'upload su Drive:\n{0}", ["en"] = "Local save succeeded, but error during upload to Drive:\n{0}" },
 ["Publish_Completed"] = new() { ["it"] = "Il progetto '{0}' e ora condiviso su Google Drive!", ["en"] = "Project '{0}' is now shared on Google Drive!" },
 ["Publish_CompletedContent"] = new() { 
 ["it"] = "E stato creato il tuo modello locale in:\n{0}\n\nVuoi invitare subito i tuoi colleghi (inserendo la loro email) per consentire loro di aprire e sincronizzare il progetto?",
 ["en"] = "Your local model has been created at:\n{0}\n\nWould you like to invite teammates now to allow them to open and sync the project?"
 },
 ["Sync_Title"] = new() { ["it"] = "Sincronizza Drive", ["en"] = "Sync with Drive" },
 ["Sync_Success"] = new() { ["it"] = "Modifiche sincronizzate localmente e caricate con successo su Google Drive!", ["en"] = "Changes synchronized locally and successfully uploaded to Google Drive!" },
 ["Sync_Error"] = new() { ["it"] = "Sincronizzazione interrotta o fallita:\n{0}", ["en"] = "Synchronization interrupted or failed:\n{0}" },
 ["Status_Title"] = new() { ["it"] = "Stato Drive Connector & Sincronizzazione", ["en"] = "Drive Connector & Sync Status" },
 ["Status_Main"] = new() { ["it"] = "Google Drive Connector v1.0 - Stato del Sistema", ["en"] = "Google Drive Connector v1.0 - System Status" },
 ["Status_ForceUnlock"] = new() { ["it"] = "Verifica o Rimuovi Lock di Sincronizzazione (Force Unlock)", ["en"] = "Check or Remove Sync Lock (Force Unlock)" },
 ["Status_ForceUnlockDesc"] = new() { ["it"] = "Rimuove forzatamente eventuali lock orfani lasciati da crash o blackout improvvisi di un PC del team.", ["en"] = "Forcefully removes orphan locks left by crashes or sudden shutdowns." },
 ["Borrow_SelectElement"] = new() { ["it"] = "Seleziona almeno un elemento nella vista (muro, porta, finestra, ecc.) prima di cliccare.", ["en"] = "Please select at least one element in the view first." },
 ["Borrow_OwnedBy"] = new() { ["it"] = "L'elemento e di proprieta di: {0}", ["en"] = "Element is owned by: {0}" },
 ["Borrow_AskRequest"] = new() { ["it"] = "Vuoi inviare una richiesta immediata a {0} per ottenere il permesso di modificare:\n{1}?", ["en"] = "Do you want to send an immediate request to {0} for permission to edit:\n{1}?" },
 ["Borrow_SentSuccess"] = new() { ["it"] = "Richiesta di prestito inviata con successo a {0}!", ["en"] = "Borrow request sent successfully to {0}!" },
 ["Borrow_AlreadyYours"] = new() { ["it"] = "Gli elementi selezionati ({0}) sono gia tuoi o non richiedono prestito.", ["en"] = "Selected elements ({0}) are already yours or do not require borrowing." },

 // Common
 ["Common_Close"] = new() { ["it"] = "Chiudi", ["en"] = "Close" },
 ["Common_Cancel"] = new() { ["it"] = "Annulla", ["en"] = "Cancel" },
 ["Common_Save"] = new() { ["it"] = "Salva", ["en"] = "Save" },
 ["Common_Exit"] = new() { ["it"] = "Esci", ["en"] = "Exit" },
 ["Common_Error"] = new() { ["it"] = "Errore", ["en"] = "Error" },
 ["Common_Success"] = new() { ["it"] = "Operazione completata con successo.", ["en"] = "Operation completed successfully." },
 ["Common_RoleOwner"] = new() { ["it"] = "Proprietario", ["en"] = "Owner" },
 ["Common_RoleEditor"] = new() { ["it"] = "Editor (Modifica & Sincronizza)", ["en"] = "Editor (Edit & Sync)" },
 ["Common_RoleViewer"] = new() { ["it"] = "Visualizzatore (Sola Lettura)", ["en"] = "Viewer (Read Only)" },

 // Live Element Locks
 ["Settings_EnableLiveLocks"] = new() { ["it"] = "Attiva blocco live elementi in modifica (stile LAN)", ["en"] = "Enable live element edit locking (LAN-style)" },
 ["LiveLock_Title"] = new() { ["it"] = "Elemento in modifica live da un collega", ["en"] = "Element currently edited live by a teammate" },
 ["LiveLock_BlockedDetail"] = new() {
 ["it"] = "Questo elemento e attualmente in modifica sul PC di '{0}' ({1}) ma non e ancora stato sincronizzato.\n\nAttendi che {0} sincronizzi su Drive oppure usa 'Richiedi Prestito'.",
 ["en"] = "This element is currently being edited on '{0}' ({1}) and has not been synchronized yet.\n\nPlease wait for {0} to sync to Drive or use 'Request Borrow'."
 },
 ["LiveLock_FailureStatic"] = new() {
 ["it"] = "Elemento bloccato in modifica live da un collega su Google Drive. Annulla questa operazione e attendi che il collega sincronizzi il modello.",
 ["en"] = "Element is locked for live editing by a teammate on Google Drive. Cancel this operation and wait for your teammate to synchronize."
 }
 };
 }
}
