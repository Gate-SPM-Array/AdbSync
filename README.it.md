# AdbSync v0.1.3

[English](README.md) · **Italiano**

> **Lingua:** il programma parla **italiano** (incorporato) e **inglese** (`lang\en.json`). Si sceglie dal menu **🌐 Lingua** e si applica al riavvio. Chiunque può aggiungere una lingua con un file JSON: vedi [Traduzioni](#10-traduzioni).

Copia **incrementale** dei file da uno smartphone Android al PC, via **ADB** invece che via MTP.
Confronta le due cartelle come FreeFileSync e copia **solo i file nuovi o modificati**, con verifica, ritentativi automatici e uno stato dettagliato.

Modalità di questa versione: **Aggiorna, telefono → PC**. Copia i file nuovi e quelli modificati e **non cancella mai nulla**, né sul PC né sul telefono.

---

## 1. Preparazione (una volta sola)

1. **Sul telefono**: Impostazioni → Info sul telefono → tocca 7 volte "Numero build" per sbloccare le Opzioni sviluppatore.
   Poi Impostazioni → Opzioni sviluppatore → attiva **Debug USB**.
2. Collega il telefono con il cavo USB. Sul telefono compare "Consentire il debug USB?": spunta *Consenti sempre da questo computer* e premi **Consenti**.
3. **Sul PC**: estrai lo zip in una cartella a tua scelta e avvia `AdbSync.exe`. Se adb non è presente, il programma propone di scaricare le *platform-tools* ufficiali Google (circa 7 MB) nella sottocartella `platform-tools` del programma. In alternativa puoi indicare un `adb.exe` già installato.

**Programma portable**: impostazioni, log, report e platform-tools stanno tutti nella cartella del programma:

```
AdbSync\
  AdbSync.exe, adbsync-cli.exe, README.md (EN), README.it.md (IT), CHANGELOG.md
  adbsync.ini      collegamenti del menu Aiuto (pagina del progetto, guida online, segnalazioni)
  lang\en.json     traduzione inglese (qui si aggiungono altre lingue)
  data\settings.json
  logs\            log, report CSV, elenchi nomi_convertiti_*, cartelle diagnostica_*
  platform-tools\  adb.exe e DLL (scaricate da Google)
```

Se la cartella non è scrivibile (es. `C:\Program Files`) il programma ripiega su `%LOCALAPPDATA%\AdbSync` e lo segnala nel log.
La chiave di autorizzazione adb resta nella posizione standard di Windows (`%USERPROFILE%\.android`).
Per spostare o cancellare la cartella del programma mentre adb è attivo: menu *adb… → Arresta il server adb*.

> Il pacchetto `AdbSync_v0.1.2_win-x64.zip` richiede il **.NET 10 Desktop Runtime x64**, che si scarica da https://dotnet.microsoft.com/download/dotnet/10.0

## 2. Uso

1. Scegli il **telefono**, la **cartella sul telefono** (es. `/sdcard/DCIM`; *Sfoglia…* naviga le cartelle del telefono) e la **cartella sul PC**.
2. Premi **🔍 Confronta**. In pochi secondi vedi l'elenco dei file divisi per categoria:

   | Categoria | Significato | Azione |
   |---|---|---|
   | Nuovo | esiste solo sul telefono | copiato |
   | Modificato | il telefono ha una versione più recente | copiato (sovrascrive) |
   | Conflitto | il PC ha una versione più recente **e diversa** | non toccato, salvo l'opzione *Sovrascrivi i conflitti* |
   | Solo data diversa | stesso nome e stessa dimensione, data diversa (tipico dopo vecchie copie via MTP) | non toccato, salvo l'opzione *Allinea date stessa dimensione* (vedi sotto) |
   | Identico | stessa dimensione e data | nessuna |
   | Solo sul PC | non c'è sul telefono | nessuna (mai cancellato) |
   | Collisione nome | due file del telefono che su Windows avrebbero lo stesso nome | saltato e segnalato |
   | Escluso | corrisponde a una regola di esclusione (es. `.thumbnails/`), sul telefono o solo sul PC | mai toccato; la colonna Note mostra la regola |

   Con il tasto destro sull'elenco puoi escludere o includere singoli file: la scelta resta valida anche quando il piano viene ricalcolato, fino al prossimo Confronta. Il doppio clic apre il file in Esplora risorse.

   Sopra l'elenco c'è il riepilogo dei conteggi, **file e cartelle** separati per telefono e PC, da confrontare direttamente con FreeFileSync. FreeFileSync conta le cartelle come elementi.

   **Elenco acquisito, niente nuove scansioni.** Dopo il Confronta l'elenco di telefono e PC resta in memoria. Se cambi un'opzione (confronto, conflitti, esclusioni, allineamento date) il piano si ricalcola dall'elenco in pochi secondi, senza riscansionare. Si riscansiona solo cambiando telefono o cartelle, oppure premendo di nuovo Confronta. Con 1.000.000 di file l'elenco occupa circa 400 MB di RAM e il ricalcolo richiede alcuni secondi.
3. Premi **▶ Sincronizza**. Il riquadro *Stato* mostra:
   - la percentuale e i byte copiati sul totale;
   - la velocità e il tempo stimato rimanente;
   - i file copiati, gli errori definitivi e quelli in attesa di un nuovo tentativo;
   - per ogni trasferimento in corso: il file, la percentuale, la velocità e il numero del tentativo;
   - i messaggi di attesa, ad esempio "Telefono non raggiungibile: in attesa della riconnessione…".
4. Alla fine compare un **esito esplicito**:
   - **COMPLETATO** solo se *tutti* i file previsti sono stati copiati e verificati;
   - altrimenti **TERMINATO CON ERRORI**, **INTERROTTO** o **ANNULLATO**, con l'elenco dei file mancanti e il motivo nella scheda *Errori*.

   Dopo la copia l'elenco in memoria viene aggiornato con quello che il programma ha scritto, e compare la finestra **"Altre operazioni sull'elenco acquisito"**:
   - **Riprova i N file non copiati**: riparte subito, senza nuova scansione;
   - **Allinea le date** dei file "solo data diversa", con il livello di verifica scelto e solo per quella operazione;
   - **Chiudi**.

   In alternativa, ripeti **Confronta → Sincronizza**: vengono copiati solo i file che mancano.

## 3. Come lavora (perché è affidabile)

- **Scansione veloce e verificata**: un solo comando sul telefono restituisce l'intero albero, file e cartelle, con dimensione e data al nanosecondo. I dati arrivano a blocchi di al massimo 500 percorsi (64 KB), quindi è impossibile superare il limite degli argomenti di Android.
  - Ogni blocco dichiara quanti elementi contiene, e il programma verifica che il totale ricevuto coincida.
  - Qualsiasi errore di scansione, o un conteggio che non torna, **ferma il confronto** con un messaggio esplicito: un elenco parziale non diventa mai un piano di copia.
  - Fanno eccezione i soli "permesso negato", tipici di `Android/data` e `Android/obb`. Vengono elencati nella scheda Errori e il riepilogo finale non dice "COMPLETATO" ma "COMPLETATO CON AVVISI".
  - Il numero di elementi trovati viene contato in modo indipendente e confrontato con quelli ricevuti. Anche un blocco interrotto sul telefono senza alcun messaggio viene rilevato.
- **Protezione delle modifiche sul PC**: subito prima di scrivere, il programma verifica che il file sul PC sia ancora com'era al momento del confronto. Se nel frattempo è comparso o è stato modificato, non viene toccato e viene segnalato.
- **Collegamenti sul PC** (junction/symlink): non vengono attraversati e non vi si scrive dentro, per non uscire mai dalla cartella di destinazione.
- **Trasferimento**: usa il protocollo *sync* di ADB direttamente, tramite la libreria AdvancedSharpAdbClient. Non lancia un processo `adb.exe` per ogni file.
- **Copia atomica**: ogni file viene scritto come `nome.adbsync-part`. Diventa il file definitivo solo dopo i controlli:
  - la dimensione ricevuta corrisponde a quella attesa;
  - con *Verifica MD5* attiva, l'MD5 calcolato sul telefono coincide con quello calcolato sul PC.

  Poi vengono impostate le date. Un file interrotto a metà non sostituisce mai quello buono.
- **Date fedeli all'originale**:
  - file: data di modifica del telefono con la frazione di secondo; la data di creazione viene posta uguale alla data di modifica, perché quella vera del telefono non è leggibile;
  - cartelle: data di modifica del telefono (e creazione uguale), impostata alla fine della copia;
  - file identici già presenti sul PC: le date vengono riallineate senza ricopiare il contenuto.
- **Cartelle vuote**: vengono ricreate, così la struttura è identica a quella del telefono.
- **Stalli**: se per *Timeout stallo* secondi (minimo 1, predefinito 30) non arriva alcun byte, il trasferimento viene chiuso. Il file **torna in fondo alla coda, senza attese**, e viene ritentato dopo gli altri.
  - Un timeout basso con molti tentativi di solito rende di più: un file "incastrato" non blocca mai un trasferimento per tanto tempo.
  - Dopo molti stalli di fila senza alcun file completato (almeno 10, oppure 4 × trasferimenti paralleli), il programma verifica che il telefono risponda. Se il problema continua per 5 controlli di fila, il lavoro si interrompe con un messaggio chiaro invece di girare all'infinito.
  - Nel riepilogo finale e nel log: numero di stalli, tempo perso, file riusciti dopo più tentativi, file **sempre bloccati** (candidati all'esclusione).
- **Ritentativi**: fino a *1 + Tentativi extra* volte per file (predefinito 6). Dopo un errore di collegamento si attende in modo crescente (2, 5, 10, 20, 30 s); dopo uno stallo no. Gli errori che non si risolvono ritentando, come un file cancellato dal telefono o un permesso negato, vengono segnalati subito.
- **Allineamento date dei file "solo data diversa"** (stesso nome, stessa dimensione al byte). Livelli:
  - *No*: non toccati (predefinito);
  - *Solo dimensione*: date allineate senza leggere il contenuto;
  - *Verifica rapida*: MD5 dei primi e degli ultimi 64 KB, calcolato sul telefono e sul PC; esatta per i file fino a 128 KB;
  - *Verifica MD5 completa*: legge tutto il file, più lenta.

  Sul telefono gli hash si calcolano **a blocchi**: un solo comando ogni 200 file al massimo, non un comando per file. Se il contenuto è diverso, il file non viene toccato, viene segnalato in arancione nella scheda Errori e da quel momento risulta *Conflitto*. Se la verifica non riesce (file illeggibile, telefono che non risponde), le date restano com'erano e il file viene segnalato.
- **Cavo staccato o telefono bloccato**: la copia si mette in attesa e riprende da sola quando il telefono torna disponibile (attesa massima 5 minuti, poi interruzione pulita).
- **Disco di destinazione staccato**: la copia va in pausa con il messaggio "Disco di destinazione non disponibile". Riprende da sola se il disco torna con la stessa lettera di unità, altrimenti si interrompe in modo pulito dopo 5 minuti, senza consumare i tentativi dei file.
- **Spazio su disco**: viene controllato prima di iniziare. Se finisce durante la copia, il lavoro si interrompe con un messaggio chiaro.
- Il **PC non va in sospensione** durante la copia.
- I **nomi non validi su Windows** (`: ? * " < > |`, punti o spazi finali, `CON`, `AUX`…) vengono salvati con caratteri equivalenti, ad esempio `：` e `？`. La conversione è stabile, quindi la copia incrementale continua a funzionare.
- **Esclusioni predefinite**:
  - `.thumbnails/` (miniature generate da Android);
  - `.trashed-*` (file nel cestino di Android 11+);
  - `.pending-*` (file in fase di scrittura);
  - i propri file temporanei.

  Si modificano con il pulsante *Esclusioni…*. Le regole valgono **a partire dalla cartella scelta sul telefono**: scegliendo `/sdcard/Pictures` la cartella `.thumbnails/` al suo interno è esclusa; scegliendo direttamente `/sdcard/Pictures/.thumbnails`, no. I file esclusi sono contati nel riepilogo del confronto e si vedono con il filtro *Esclusi*: un "niente da copiare" non è mai un mistero.
- **Elenco dei nomi convertiti**: ogni sincronizzazione con nomi convertiti scrive `logs\nomi_convertiti_<data>.txt` con tipo, percorso originale sul telefono, percorso sul PC ed esito, cartelle comprese. Così si risale sempre al nome originale di un file.

Il riquadro *Stato* si ridimensiona trascinando il divisore sopra di esso, e la posizione viene ricordata. L'elenco dei trasferimenti in corso mostra sempre tutti i trasferimenti paralleli.

## 4. Opzioni

| Opzione | Predefinito | Note |
|---|---|---|
| Confronta solo la dimensione | no | utile se i file sul PC sono stati copiati in passato senza conservare la data |
| Ignora differenze di 1 ora | sì | dischi FAT/exFAT e cambio dell'ora legale |
| Sovrascrivi i conflitti | no | il telefono "vince" anche se il PC è più recente |
| Verifica MD5 | no | massima garanzia di integrità, ma più lento |
| Trasferimenti paralleli | 2 | da 2 a 4 aiuta con molti file piccoli (es. WhatsApp) |
| Timeout stallo | 30 s | minimo 1 s; il file bloccato torna in fondo alla coda |
| Tentativi extra | 5 | |
| Allinea date stessa dimensione | No | No / Solo dimensione / Verifica rapida / Verifica MD5 completa |
| Modalità diagnostica | no | a ogni confronto salva in `logs\diagnostica_<data>\` l'elenco completo telefono, l'elenco PC, il piano con il motivo di ogni decisione e i messaggi grezzi del telefono |

## 5. Riga di comando

`adbsync-cli.exe` usa lo stesso motore ed è adatta a script e attività pianificate:

```
adbsync-cli devices
adbsync-cli compare -r /sdcard/DCIM -l "D:\Backup\Telefono\DCIM" --list
adbsync-cli sync    -r /sdcard/DCIM -l "D:\Backup\Telefono\DCIM" --yes --verify
adbsync-cli sync    -r /sdcard -l "D:\Backup\Telefono" --parallel 4 --stall 5 --retries 20 --align-dates quick
adbsync-cli --help
```

Codici di uscita: `0` tutto eseguito · `1` errori su alcuni file · `2` interrotto o scansione incompleta · `3` errore di utilizzo. Opzione `--diagnostic` per gli elenchi completi; `--align-dates none|size|quick|full` per l'allineamento delle date.

## 6. Log e report

- Log completo di ogni sessione: `logs\adbsync_AAAAMMGG_hhmmss.log` nella cartella del programma.
- Report CSV di ogni sincronizzazione, apribile con Excel: `report_AAAAMMGG_hhmmss.csv`, con l'esito file per file.
- Impostazioni: `data\settings.json` nella cartella del programma.
- Modalità diagnostica: `logs\diagnostica_AAAAMMGG_hhmmss\` con `1_telefono.tsv`, `2_pc.tsv`, `3_piano.tsv` e `4_messaggi_telefono.txt`.

## 7. Limiti noti di questa versione

- L'elenco acquisito vive in memoria finché il programma resta aperto: chiudendolo, al riavvio serve un nuovo Confronta.
- La *Verifica rapida* non vede differenze che stanno solo a metà di un file più grande di 128 KB. Per la massima garanzia usa *Verifica MD5 completa*.
- I file più piccoli sul PC ma più recenti (per esempio copie MTP interrotte) risultano *Conflitto* e non vengono copiati, salvo l'opzione *Sovrascrivi i conflitti*. Si individuano con il filtro Conflitto.

- Solo direzione telefono → PC, solo modalità *Aggiorna* (niente *Mirror*, niente cancellazioni).
- Da Android 11, `Android/data` e `Android/obb` non sono leggibili via ADB senza root: vengono segnalate come avvisi nel log.
- Senza *Verifica MD5*, una corruzione che lascia invariata la dimensione del file non è rilevabile. È un caso molto raro, perché ADB usa già un protocollo affidabile su USB.
- Con più telefoni collegati si sceglie quello da usare dall'elenco.
- In inglese i plurali non sono gestiti ("1 files").
- La data di creazione originale dei file del telefono non è leggibile via ADB: viene posta uguale alla data di modifica.
- Se il disco di destinazione torna con una lettera di unità diversa, la copia non lo ritrova: va ricollegato con la stessa lettera, oppure basta rifare Confronta → Sincronizza indicando il nuovo percorso.

## 8. Struttura del codice

```
src/AdbSync.Core   motore: scansione, confronto, copia robusta (multipiattaforma)
src/AdbSync.Gui    interfaccia Windows Forms
src/AdbSync.Cli    riga di comando
lang/              file di traduzione (en.json = riferimento)
tests/             96 test, incluso un telefono simulato con guasti (stalli, disconnessioni, copie troncate, ...)
```

Compilazione: .NET 10 SDK → `dotnet build`. Test: `dotnet test`.

Pubblicazione:

```
dotnet publish src/AdbSync.Gui -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 9. Licenza

Rilasciato con licenza [MIT](LICENSE). Componenti di terze parti: vedi [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## 10. Traduzioni

Tutti i testi del programma passano dai file `lang\<codice>.json` accanto all'eseguibile. Per aggiungere una lingua:

1. Copia `lang\en.json` in `lang\<codice>.json`, per esempio `de.json`.
2. Imposta `"code"`, `"language"` (il nome mostrato nel menu), `"culture"` (formato di numeri e date, per esempio `de-DE`) e `"translators"`.
3. Traduci i **valori** in `"strings"`. Le chiavi sono i testi italiani originali e non vanno modificate. Lascia identici i segnaposto come `{0}` o `{1:0.0}`: una traduzione che li altera viene ignorata e segnalata nel log.
4. Riavvia AdbSync e scegli la lingua. I testi non tradotti restano in italiano, e il log dice quanti ne mancano.

Le pull request con nuove lingue sono benvenute.

## 11. Crediti

Sviluppato da [Gate-SPM-Array](https://github.com/Gate-SPM-Array) con [Claude](https://claude.com) (Anthropic), che ha scritto il codice sulla base di requisiti, scelte di progetto e collaudo su telefoni reali del maintainer.

AdbSync è un progetto indipendente, non affiliato a FreeFileSync né ad AdbFileManager: il primo è il riferimento funzionale per la logica di confronto, il secondo ha ispirato l'uso di ADB al posto di MTP.
