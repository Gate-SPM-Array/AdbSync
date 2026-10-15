# Changelog

## v0.1.3 — 2026-10-15

### Novità
- **Lingue con file esterni.** Tutti i testi (interfaccia, log, riepiloghi, riga di comando) passano dai file `lang\<codice>.json` accanto al programma, modificabili da chiunque senza ricompilare.
  - Incluso l'**inglese** (`lang\en.json`); l'italiano resta incorporato e predefinito.
  - Si sceglie dal menu della lingua in alto a destra; la scelta si applica al riavvio. CLI: `--lang en`, la variabile `ADBSYNC_LANG` oppure la lingua scelta nell'interfaccia grafica.
  - Un testo non tradotto resta in italiano; una traduzione che altera i segnaposto `{0}` viene ignorata e segnalata nel log.
  - Numeri e date seguono il formato della lingua.
- **File esclusi visibili.** Prima i file scartati dalle regole di esclusione (per esempio le miniature `.thumbnails/`) sparivano senza traccia, e "0 da copiare" sembrava un errore della scansione.
  - Ora compaiono nella categoria **Esclusi**, con la regola che li ha esclusi; sono compresi i file presenti solo sul PC.
  - I conteggi sono nel riepilogo del confronto e nel log, regola per regola.
  - La finestra Esclusioni spiega le regole predefinite e che le regole valgono a partire dalla cartella scelta sul telefono.
- **Elenco dei nomi convertiti.** Quando un nome contiene caratteri non ammessi su Windows (exFAT/NTFS), ogni sincronizzazione scrive `logs\nomi_convertiti_<data>.txt`. Contiene il tipo, il percorso originale sul telefono, il percorso salvato sul PC e l'esito, anche per le cartelle. Il percorso del file compare nel riepilogo finale e nella riga di comando.

### Altro
- Il controllo della risposta del telefono usa `printf` invece di `echo`.
- Test: 96, più 17 nuovi per lingue, esclusi e nomi convertiti. Un test verifica che ogni testo del codice sia tradotto in `en.json` con gli stessi segnaposto.


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
