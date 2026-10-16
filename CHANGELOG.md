# Changelog

Regola: ogni versione consegnata ha un numero diverso; anche una modifica minima del codice produce una nuova versione con la sua voce qui sotto.

## v0.1.7 — 2026-10-16

### Novità
- **Log divisi per sessione.** Prima tutti i log, i report e la diagnostica finivano insieme nella cartella `logs`. Ora ogni sessione ha la sua cartella: `logs\<data_ora>_<produttore-modello>_<nome del telefono>\`, per esempio `2026-10-16_14-30-05_Xiaomi-2201117TG_Telefono-di-David`.
  - Una sessione inizia quando il programma è aperto e si collega un telefono, e dura finché il programma resta aperto con lo stesso telefono. Un telefono diverso apre una nuova sessione. Ogni avvio di `adbsync-cli` è una sessione.
  - Dentro la cartella: `sessione.log`, i report CSV, l'elenco dei nomi convertiti e le cartelle della modalità diagnostica.
  - Il nome del telefono è quello dato nelle impostazioni di Android (o il nome Bluetooth). Il numero di serie non compare, salvo le ultime 4 cifre se sono collegati due telefoni dello stesso modello.
- **SystemLOG** (`logs\SystemLOG\system_<anno-mese>.log`, un file al mese): avvio e chiusura del programma con versione, Windows, .NET e lingua; adb; telefoni rilevati e scollegati; cartella di sessione aperta; errori imprevisti e crash, anche prima che compaia la finestra. Interfaccia grafica e riga di comando possono scriverci insieme (`[GUI]`, `[CLI]`).
- **Stato della cartella log all'apertura**: numero di sessioni e dimensione totale, nel riquadro Stato e nel SystemLOG. Nessuna cancellazione automatica.
- Menu *File → Apri cartella della sessione corrente*.

### Altro
- I log delle versioni precedenti restano dove sono.
- Test: 126, di cui 14 nuovi (nome della cartella, lettura del nome del telefono anche con la vera mksh, sessioni, SystemLOG scritto da due programmi insieme, statistiche della cartella).

## v0.1.6 — 2026-10-16

Risolve il "loop" dei file grandi (copia che si fermava intorno a 1,8–2,2 GB e ripartiva da zero). La causa, trovata con il diario della 0.1.5D, era il disco di destinazione del PC: alcune scritture restavano ferme da 5 a 55 s, il programma le scambiava per uno stallo del telefono, cancellava il parziale e ricominciava.

### Novità
- **Due timeout separati.**
  - *Timeout stallo telefono* (finestra principale, minimo 1 s, predefinito 30): conta solo il tempo in cui il telefono non manda dati.
  - *Timeout scrittura su disco (PC)* (menu *Strumenti*, minimo 30 s, predefinito 120): quanto può restare ferma una singola scrittura sul disco del PC. CLI: `--disk-stall <secondi>`.
  - Il tempo di scrittura su disco non conta più come stallo del telefono. Un blocco del disco oltre il suo limite rimette il file in coda senza far scattare il controllo del telefono.
- **Stato "Disco di destinazione lento"**: dopo 3 s di scrittura ferma lo stato lo dice, con i secondi trascorsi e il limite, e la copia aspetta.
- **Ripresa senza riscrivere.** Dopo uno stallo o un errore di collegamento il parziale `.adbsync-part` viene tenuto. Al tentativo successivo il telefono rimanda il file dall'inizio (il protocollo ADB non permette di partire a metà), ma i byte già scritti sul PC, meno 1 MiB di margine, non vengono riscritti: il disco lavora molto meno.
  - Il parziale si riusa solo se dimensione e data sul telefono sono rimaste uguali.
  - Copia corrotta (MD5 diverso), errore del disco locale o errore definitivo: il parziale viene cancellato e si riparte da zero.
  - Alla fine della copia i parziali dei file non completati vengono cancellati.

### Diagnosi
- Il diario delle pause e degli stalli (`[diagnosi] …`) e il protocollo v2 della 0.1.5D restano. Il costo è trascurabile: qualche lettura di contatori per ogni blocco di dati.

### Altro
- L'etichetta "Timeout stallo (s)" diventa "Timeout stallo telefono (s)". Il minimo di 1 s ora vale anche per il valore salvato nelle impostazioni.
- Test: 112, di cui 5 nuovi (disco lento entro il limite, disco bloccato oltre il limite, ripresa senza riscrivere, copia corrotta mai ripresa, parziali cancellati).

## v0.1.5D — 2026-10-16 (diagnosi)

Versione di diagnosi, cioè la 0.1.5 più gli strumenti per capire le pause nei trasferimenti dei file grandi. Il comportamento di copia non cambia.

### Diagnosi
- **Diario delle pause nel log.** Ogni interruzione del flusso di dati oltre 0,5 s viene registrata come `[diagnosi] pausa di … ms`, con:
  - file, byte ricevuti sul totale;
  - tempo passato a **scrivere su disco** e tempo passato ad **aspettare dati da adb**;
  - pause del garbage collector .NET e stato del thread pool, cioè le possibili cause interne al programma.
- **Dettaglio di ogni stallo** (`[diagnosi] stallo …`): se in quel momento il programma stava scrivendo su disco oppure aspettava dati da adb, con gli stessi indicatori del processo.
- **Protocollo di trasferimento ADB v2**, lo stesso di `adb pull`, attivabile da *Strumenti → Protocollo di trasferimento ADB v2*. Riga di comando: `--v2`. Se il telefono non lo supporta si torna da soli alla v1. Il protocollo in uso è scritto nel log all'inizio della copia.
- Test: 107.

## v0.1.5 — 2026-10-15

### Migliorato
- **Elenco dei nomi convertiti: sostituzioni in chiaro.** I caratteri sostitutivi (`？` `．` `＜`…) sono quasi identici agli originali e non si notano. Ora ogni riga di `nomi_convertiti_<data>.txt` ha una quinta colonna che li descrive, per esempio `"?" → "？" (U+FF1F)`, `punto finale → "．" (U+FF0E)` o nome riservato con `_` davanti. In testa al file c'è una riga che spiega il perché.

### Altro
- Un test verifica che il numero di versione sia lo stesso in progetti, interfaccia, riga di comando e CHANGELOG.
- Test: 104.

## v0.1.4 — 2026-10-15

### Novità
- **Barra dei menu**:
  - *File*: cartelle dei log e del programma, Esci;
  - *Strumenti*: Esclusioni e comandi adb;
  - *🌐 Lingua*: scelta della lingua (sostituisce la casella in alto a destra) e cartella delle lingue;
  - *Aiuto*: guida online (F1), guida nel programma (README), pagina del progetto su GitHub, segnalazioni, versioni, Informazioni.
  - Gli indirizzi non sono scritti nel codice: si leggono da `adbsync.ini` accanto al programma, e chi pubblica una versione derivata cambia solo quel file.
- **Pulsanti più chiari**: 🔍 *Confronta* (analizza, non scrive nulla), ▶ *Sincronizza* (avvia la copia), ■ *Annulla* (ferma). Tolti i numeri ①②.

## v0.1.3 — 2026-10-15

### Novità
- **Lingue con file esterni.** Tutti i testi (interfaccia, log, riepiloghi, riga di comando) passano dai file `lang\<codice>.json` accanto al programma, modificabili da chiunque senza ricompilare.
  - Incluso l'**inglese** (`lang\en.json`); l'italiano resta incorporato e predefinito.
  - La scelta si applica al riavvio. CLI: `--lang en`, la variabile `ADBSYNC_LANG` oppure la lingua scelta nell'interfaccia grafica.
  - Un testo non tradotto resta in italiano; una traduzione che altera i segnaposto `{0}` viene ignorata e segnalata nel log.
  - Numeri e date seguono il formato della lingua.
- **File esclusi visibili.** Prima i file scartati dalle regole di esclusione (per esempio le miniature `.thumbnails/`) sparivano senza traccia, e "0 da copiare" sembrava un errore della scansione.
  - Ora compaiono nella categoria **Esclusi**, con la regola che li ha esclusi; sono compresi i file presenti solo sul PC.
  - I conteggi sono nel riepilogo del confronto e nel log, regola per regola.
  - La finestra Esclusioni spiega le regole predefinite e che le regole valgono a partire dalla cartella scelta sul telefono.
- **Elenco dei nomi convertiti.** Quando un nome contiene caratteri non ammessi su Windows (exFAT/NTFS), ogni sincronizzazione scrive `logs\nomi_convertiti_<data>.txt`, con il tipo, il percorso originale sul telefono, il percorso salvato sul PC e l'esito, anche per le cartelle. Il percorso del file compare nel riepilogo finale e nella riga di comando.

### Altro
- Il controllo della risposta del telefono usa `printf` invece di `echo`.
- Test: 96, di cui 17 nuovi per lingue, esclusi e nomi convertiti. Un test verifica che ogni testo del codice sia tradotto in `en.json` con gli stessi segnaposto.

## v0.1.2 — 2026-10-15

### Novità
- **Stalli gestiti con la coda.** Un file in stallo torna in fondo alla coda e viene ritentato dopo gli altri, senza attese crescenti. Le attese restano solo per gli errori di collegamento. Il timeout di stallo minimo scende da 5 s a 1 s.
- **Statistiche stalli** nel riepilogo e nel log: numero di stalli, tempo perso, file riusciti dopo più tentativi, file sempre bloccati (candidati all'esclusione).
- **Allineamento date dei file "solo data diversa"**, cioè stesso nome e stessa dimensione al byte. Livelli: no, solo dimensione, verifica rapida (MD5 dei primi e ultimi 64 KB, esatta fino a 128 KB), verifica MD5 completa. Gli hash sul telefono si calcolano a blocchi: un comando ogni 200 file al massimo. I file con contenuto diverso non vengono toccati e restano segnalati.
- **Elenco acquisito riutilizzabile.** Cambiando le opzioni il piano si ricalcola dall'elenco in memoria, senza nuova scansione. Dopo ogni copia l'elenco viene aggiornato con quello che è stato scritto.
- **Finestra "Altre operazioni"** a fine copia: riprova i file non copiati, oppure allinea le date (solo per quell'operazione), senza ripartire da zero.
- Conteggi **file e cartelle** di telefono e PC nel riepilogo del confronto, per il confronto diretto con FreeFileSync.
- **Interfaccia**:
  - divisore trascinabile tra elenco e riquadro Stato, con la posizione ricordata;
  - l'elenco dei trasferimenti in corso mostra tutti i trasferimenti paralleli, anche con 4.
- **Memoria** ridotta per elenchi molto grandi: circa 400 byte per file, quindi circa 400 MB con 1.000.000 di file; il ricalcolo del piano richiede pochi secondi.
- CLI: opzione `--align-dates none|size|quick|full`; stalli mostrati nella riga di stato.

### Correzioni da revisione indipendente del codice
- Un errore persistente nel calcolo degli hash sul telefono interrompeva il lavoro senza riepilogo. Ora i file di quel blocco vengono segnalati come non verificati, con le date intatte, e il lavoro prosegue.
- Una verifica interrotta non viene mai conteggiata come riuscita.
- MD5 completo: blocchi limitati anche a circa 2 GB di dati, per restare nel tempo massimo del comando.
- Sul telefono un errore di lettura durante il calcolo dell'hash ora annulla l'hash (`pipefail`), invece di produrre l'MD5 di dati parziali scambiato per "contenuto diverso".
- Stalli continui senza alcun file completato: verifica che il telefono risponda e, se non si risolve, interruzione pulita invece di un ciclo senza fine.
- Le scelte manuali "Non copiare" restano valide quando il piano viene ricalcolato, anche dopo una copia e anche nel pulsante "Riprova".
- Un file cambiato sul telefono durante la copia non risulta più "Conflitto" nel piano successivo.
- Cartelle scritte durante un'esecuzione interrotta: le date vengono riallineate al giro successivo.


## v0.1.1 — 2026-10-14

### Correzioni gravi
- **Scansione incompleta del telefono con migliaia di file.** Con cartelle grandi, ad esempio `/sdcard/Android`, alcuni blocchi della scansione fallivano sul telefono con "Argument list too long". Il programma lo segnalava solo come avviso e proseguiva, dichiarando "già aggiornata" una cartella a cui mancavano decine di migliaia di file.
  - Ora la scansione lavora a blocchi limitati: al massimo 500 percorsi e 64 KB per blocco.
  - Il numero di elementi trovati viene verificato contro quelli ricevuti.
  - Qualunque errore che non sia un "permesso negato" **ferma il confronto** con un messaggio esplicito.
- **Disco di destinazione staccato durante la copia.** Prima ogni file veniva ritentato per circa un minuto, all'infinito. Ora la copia va in pausa, riprende da sola quando il disco torna e si interrompe in modo pulito se non torna. La cartella di destinazione non viene più ricreata mentre il disco è assente.

### Correzioni da revisione indipendente del codice
- La scansione ora conta in modo **indipendente** gli elementi trovati da `find` e li confronta con quelli ricevuti.
  - Ogni blocco ha un'intestazione e una chiusura.
  - Vengono riportati i codici di uscita reali di `find` e `xargs`.
  - Un blocco ucciso sul telefono senza alcun messaggio ora viene rilevato.
- Cartella sul telefono non leggibile (permesso negato sulla radice): prima produceva un elenco vuoto, cioè "già allineato"; ora ferma il confronto.
- Senza cartella temporanea sul telefono, la lettura avviene in tre passate separate: errori e dati non si mescolano mai.
- Messaggi stampati con `printf`, perché `echo` di mksh interpreta le sequenze con `\`. L'elenco errori ha un marcatore di fine. I "permesso negato" sono tollerati solo nel formato esatto.
- "COMPLETATO" compare solo se tutto ciò che è sul telefono è finito sul PC. Con elementi non copiabili (permessi, collisioni di nomi, bloccati) il riepilogo dice "COMPLETATO CON AVVISI" e li elenca.
- Un file comparso o modificato sul PC dopo il confronto non viene più sovrascritto, e nemmeno le sue date vengono ritoccate.
- Collegamenti sul PC (junction/symlink): non vengono attraversati né usati come destinazione. I segnaposto cloud (OneDrive) ora vengono letti.
- In modalità "solo dimensione" le date di un file vengono allineate solo se anche la data di modifica coincide.
- I file del telefono che terminano con `.adbsync-part` vengono sempre esclusi e segnalati.
- La scansione si ferma con un errore se il telefono non invia dati per 3 minuti.
- I link simbolici del telefono vengono contati e segnalati.

### Novità
- Date fedeli all'originale:
  - data di modifica dei file con la frazione di secondo;
  - data di creazione uguale alla data di modifica;
  - date delle cartelle preservate;
  - date dei file identici riallineate senza ricopiarli.
- Le cartelle vuote del telefono vengono ricreate sul PC.
- **Modalità diagnostica**: elenchi completi file per file (telefono, PC, piano con il motivo di ogni decisione, messaggi grezzi del telefono).
- **Programma portable**: impostazioni, log e platform-tools nella cartella del programma. Alla prima esecuzione vengono importati quelli della v0.1.0.
- Menu adb: "Arresta il server adb", per liberare la cartella del programma.
- CLI: opzione `--diagnostic`; `compare --list` elenca anche le cartelle da creare e le date da allineare.

### Interfaccia
- Durante la scansione sono visibili il contatore degli elementi ricevuti, una barra di attività e il tempo trascorso.
- Il riquadro Stato e la scheda Errori vengono azzerati a ogni nuovo confronto. A fine copia restano fermi sul riepilogo finale, con il tempo trascorso bloccato.
- I file interrotti dopo un errore riportano "… durante il tentativo N — ultimo errore: …".
- Le opzioni sono su due righe fisse, tutte sempre visibili.

## v0.1.0 — 2026-10-14
- Prima versione: confronto telefono → PC e copia incrementale via ADB, con verifica, ritentativi, rilevamento stalli e attesa della riconnessione.
