# NCE R9 — retorno SVC/contador sobre a R8 aprovada

Preparada em 02/10/2026; implementação autorizada em 03/10/2026. O usuário aprovou expressamente R8 e a implementação R9. Versão **0.0.14-nce-r9 / 20121**, Ryujinx Neo Experimental, `org.kenjinx.android.experimental`. A base R8 é mantida junto das rodadas anteriores; a validação prática do APK R9 ainda está pendente. Não apresentar autorização de implementação como resultado de testes R9.

## Evidência e contrato limitado

Base R1 + QoL1/2/3 + R2/R3/R4/R5/R6/R7/R8 + QoL4: 57 arquivos dos manifestos sobrepostos conferidos. Current e Experimental no HEAD `d24e22d2d2714fecdae89377f1261ac49d79e3b1`, branch `neo-v2-nce2`; código aprovado local sem commits. Snapshot dos 4.432 arquivos rastreados por pasta e das fontes aprovadas capturado em `work/nce-r9/before-state.json` e `before/`.

O [changelog](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/CHANGELOG.md) e a ponta remota foram reconferidos por API: `06b96f2a7103bffe44a63bea01e0a774e8ce2d7c`, blob `b3b4c60d591eac02720aa5747c919063de60696d`. Não importar seu reader NCZ/libzstd ou a PR6. A busca por diferenças parte do código atual/histórico, não de patches descartados. O retorno de `WriteManagedCall` e os helpers introduzidos em `e8e4c5aa61` continuaram com publicação de estado nativo antes de SP; FP/NZCV foram acrescentados em `f24d6f7068`. R6 corrigiu operandos e R8 apenas a entrada. [Patcher histórico](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NcePatcher.cs).

Android NCE: SVC/contador → `WriteManagedCall` → callback → `WriteInManagedLockRelease` publica 0 → só depois restaura FP/NZCV/GuestSp. Um RequestInterrupt nesse trecho pode observar 0, marcar 2 e enviar SIGUSR2. O handler R5 aceita estado 2 como nativo, preserva FP corrente como guest e usa HostSp. Se a thread interrompida ainda está no trecho host, os valores FP podem ser host e a pilha pode ser reutilizada. A sequência é permitida pelo código; a ocorrência no aparelho não foi medida. Não atribuir o problema intermitente do Doom a esse caminho.

A proposta adiciona `WriteManagedCallReturn`, usado somente por SVC/contador. Mantém 1/3 durante restauração de FP guest e GuestSp; testa pending com LDAXR; restaura NZCV após CMP, antes do STLXR que publica 0. Reserva perdida repete leitura e restauração NZCV. Contexto vira temporariamente endereço de InManaged, com operandos distintos conforme R6; volta à base antes de continuação/callback.

Pending=3 é consumido para 1 atomicamente. Como o trecho pode já estar na pilha guest, restaura explicitamente FP host e HostSp antes do callback HLE. Callback true repete preparação guest e atende outro pedido se existir; false segue a política original: label de saída SVC ou epílogo host/Ret do contador, com SP/FP host e a frame original. SVC mantém resultados X0–X7 no storage; contador mantém TempStorage e destino. Não recalcular clock nem ignorar pending.

Pstate é carregado por LDUR W a partir do endereço temporário do estado, usando diferença calculada entre offsets (`GetPstateOffset() - GetInManagedOffset()`, hoje -20). Não mudar layout nem usar offsets de Eden. O encoder existente suporta carga de 32 bits e imediato assinado; revisar geração/execução com testes, sem aceitar apenas parsing como prova.

Os restores finais de FP/NZCV/SP saem de `WriteManagedCall` porque são executados pelo novo retorno antes da publicação. Prologue/epilogue e a aquisição R8 permanecem; a restauração final dos demais GPR/V continua na frame guest existente. Esta proposta torna coerentes SP/FP/NZCV na publicação desses dois caminhos; **não é um snapshot completo F11** e não comprova todo o protocolo F02.

## Preservação e classificação

- Correção funcional com evidência de ordem/invariante; ganho de FPS depende de medição.
- Helpers R6, entrada R4, handler R5 e wrapper R7 permanecem exatos. Retorno de sinais ainda fica separado, inclusive F03/None e classificação F04. Não alterar todos os releases por compartilhar um nome.
- Mantém serialização RequestInterrupt/Dispose, ABI/ESR, exclusivas, XZR, branches, cache/publicação, NRO, relógios e tracking. Nenhum patch de Eden/Ryubing foi portado. Os snapshots de referência permanecem os do estudo original, sem nova revalidação de suas pontas.
- Renderer, fila, compressão e envio seletivo de texturas não mudam. `TextureGroup.cs` deve manter SHA-256 `9E9720C8891523D5469214C46E7C5960CDD8960F9A79D72056B959F71F439A36` / commit 339470ed. Todas as QoL e a instalação Experimental separada permanecem.
- Telemetria R1 preservada; nenhum logger, syscall, lock, alocação ou leitura de clock novo no handler/hot path. Instrumentação das texturas continua uma proposta separada.

## Custos previstos

No retorno sem pending, o trecho antigo de release (8 instruções) mais restauração FP/NZCV/SP (8) e o novo trecho somam 16 instruções cada, por inspeção do gerador, fora do epílogo e das cargas do resultado. LDUR substitui a carga de Pstate por LDR; a ordem muda. Esta contagem precisa ser confirmada na emissão executada, não é medição de tempo.

Por atendimento pending há aproximadamente **12 instruções extras** de preparação/restauração FP e SP (guest antes da classificação, host antes do callback). Incluem cargas de contexto e MSR FPCR/FPSR; não acrescentam syscalls/locks/DMB/DSB/ISB. A frequência de pending, reservas perdidas e a duração dos callbacks determinam o custo real. Um ganho médio com piora nos quadros mais lentos reprova a rodada.

Cada retry de uma publicação 1 → 0 com reserva perdida também repete as duas instruções de carga/restauração de NZCV. Retry do consumo 3 → 1 não passa por esse restore. Medir reservas perdidas e callbacks, sem somar os custos como se todo retorno tivesse pending.

## Testes preparados e limites

Nova fixture `NceManagedReturnTests`: **24 casos preparados**. Usa entrada R4 real para criar frame host, patches SVC/contador reais com seams R8 e handler R5 real para deferral. Confere SP/FP/NZCV **na instrução que publica 0**, antes do epílogo; resultado e todos GPR/Q no retorno; destinos X0/X19/X20/X21/X22/X30; pending no callback principal, no trecho guest e entre LDAXR/STLXR; CLREX e retry; dois pedidos sucessivos; quatro entregas gerenciadas 1/3 no trecho guest; três políticas de false via frame host original.

Helpers de harness são derivados da fixture R8, que fica intocada e obrigatória. Os callbacks/contextos, invalidação de reserva e frame/restauração de sinais são sintéticos. Não instala sinais nem executa NCE real no host, mede kernel/pthread_kill/multicore/scheduler/altstack/TLS/NativeAOT ou atribui frames a um jogo. A entrega durante estado nativo real ainda requer validação Android; assert na publicação não substitui um teste de kernel. Não declarar F03 ou corrupção do Doom resolvidos.

Parsing de dois C# sem erros e hashes/checks de patches são verificações estáticas. **Codex não compilou, executou testes, gerou APK, fez commit/push ou acessou aparelho.** Testes R1–R8 e QoL4 continuam obrigatórios quando a R9 for aplicada após validação R8.

## Aplicação autorizada, validação e reversão futura

A aplicação está autorizada sobre R8 aprovada. Os hashes da base e forward-check são conferidos antes da aplicação; a conferência final usa hashes, escopos e reverse-check. O relatório `VERIFICACOES_NCE_R9.json` registra a aplicação efetiva. A preparação original foi arquivada em `work/nce-r9/pre-application`. Não há APK gerado por Codex; o prompt Antigravity atualizado parte da R9 já aplicada, sem reaplicar o patch. Divergência interrompe o trabalho, nunca reset/reaplicação silenciosa.

Comparar R9 apenas com R8 validada, mesmas cenas/save/cache/driver/limite FPS/temperatura, com sessões repetidas em Doom 2016, MK11, Zeldas e Metroid Prime 4. Observar estabilidade, pausa/retomada, input/áudio, encerramento/abrir/fechar e texturas. Doom é intermitente: cinco minutos sem falha não descartam regressão, e reaparecimento isolado não demonstra causa R9.

Reprovar por piora repetível de estabilidade/latência, pedido perdido, host callback na pilha/FP errados ou saída que não retorna uma vez. Reversão futura somente R9, restaurando base R8 de `before/`, retirando apenas fixture/documento novos e ajustando metadados para versionCode superior em APK posterior. Não usar preparação cancelada de retirada R7 ou reset geral.
