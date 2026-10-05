# NCE R6 — operandos distintos nos stores exclusivos dos helpers

Data: 2026-10-02. Base: R5 aprovada pelo usuário; sem medição quantitativa nova de FPS. Versão Experimental: **0.0.10-nce-r6 / 20117**. Pacote e nome continuam `org.kenjinx.android.experimental` e Ryujinx Neo Experimental.

## Evidência e escopo

Changelog remoto lido novamente em [923510a9558e41aee36a06d70782f9febfd14a97](https://github.com/Sunshinin11/Ryujinx_Neo/blob/923510a9558e41aee36a06d70782f9febfd14a97/CHANGELOG.md), blob `b3b4c60d591eac02720aa5747c919063de60696d`. HEAD local segue `d24e22d2d2714fecdae89377f1261ac49d79e3b1`; as rodadas aprovadas são alterações locais, ainda sem commits. Não restaurar HEAD nem importar patches descartados. O trabalho de NCZ do Hugare permanece separado.

Novo candidato F12, classificado **correção funcional necessária com evidência arquitetural**: os três helpers abaixo emitiam cinco sites STLXR com o mesmo registrador físico no endereço e no status. Por exemplo, `STLXR W20,W22,[X20]`, apesar dos nomes C# `tmp` e `tmpUint` diferentes. W20 e X20 são visões do mesmo registrador. O encoder recebe argumentos na ordem valor, endereço, status.

| Arquivo/função | Caminho que realmente chama | Sites antigos |
|---|---|---:|
| `src/Ryujinx.Cpu/Nce/NcePatcher.cs`, `WriteInManagedLockAcquire` | `WriteSvcPatch`, `WriteMrsCntpctEl0Patch`, `GenerateWrapperExceptionHandler` | 1 |
| Mesmo arquivo, `WriteInManagedLockRelease` | SVC, contador; wrapper de falha usa `ThreadExitMethod.None` | 2 |
| Mesmo arquivo, `WriteInManagedLockReleaseForSuspendHandler` | `GenerateSuspendExceptionHandler`, inclusive retorno de callback/repetição pending | 2 |

Android/Bionic usa os geradores acima no NCE ARM64. A sobreposição existe em emissão ordinária e não depende de comprovar um SIGUSR2 tardio. `git blame` atribui os helpers e encoder original a `e8e4c5aa61` (2025-05-10); `9cdd9f6dbf` (2025-09-24) mudou declarações de operandos I32 e manteve a identidade física. Links de referência: [helpers históricos](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NcePatcher.cs), [encoder histórico](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/Arm64/Assembler.cs).

Uma [correção primária do Binutils enviada por engenheiro da Arm](https://sourceware.org/pipermail/binutils/2021-March/115750.html) documenta explicitamente STLXR com base/status idênticos como imprevisível e valida as restrições de sobreposição de operandos. A página oficial Arm foi tentada e não pôde ser recuperada. Não usar a tolerância de Unicorn ou de um aparelho como prova de validade arquitetural. A falha concreta é a emissão de instrução com operandos proibidos; o comportamento observado depende do processador. Não está provado que isso causou crashes, stuttering ou oscilações de FPS anteriores.

O filtro de instruções exclusivas do guest, publicado em 27/09, resolve outro caminho e permanece intacto. A entrada R4 e a guarda R5 já emitem stores com registradores distintos e permanecem intactas. A PR6 de Hugare sobre ordem do wrapper foi consultada, mas não foi importada. A mudança de ordem de SP/ownership (F02) fica para outra rodada.

## Alteração independente

Nos três helpers, `ctx` vira temporariamente o endereço de `InManaged`, enquanto `tmp` fica disponível como status independente; `tmp2` mantém o valor. O ADD ocorre antes do label de retry. Store que falha repete com o endereço válido, mesmo que o status seja 1. Cada store bem-sucedido restaura `ctx` com SUB antes de qualquer callback ou saída. Depois de callback, o release geral recalcula o endereço para retry; false/stop também restaura a base antes de saída Label/GenerateReturn. O release do handler restaura a base antes de `lblAgain`.

Nenhum ponteiro publicado em TLS/tabela/índice muda: apenas um registrador temporário. O contexto mantém o layout existente. Callbacks respeitam os registradores callee-saved já usados; não se acrescentam registradores nem frames. Estado 0=nativo, 1=gerenciado, 2=nativo com pending, 3=gerenciado com pending continua igual. Pending é consumido pelo fluxo existente, sem nova política de coalescência, sinal ou encerramento.

`Assembler.Stlxr` agora rejeita, durante geração, status igual ao valor ou à base não-SP. Base SP e status WZR são papéis distintos apesar do encoding 31; a exceção de base SP permanece válida. A guarda não emite código extra e não altera instruções do guest.

Um método interno gera os helpers reais com contexto/callback sintéticos para testes. O parâmetro opcional de callback em release escolhe o ponteiro durante geração; as chamadas de produção mantêm o ponteiro original. Não há branch de teste no código ARM64 executado nem chamada de produção ao harness.

## Custos e separação

| Categoria | Custo adicional esperado | Limite da afirmação |
|---|---|---|
| Correção funcional | Um SUB adicional por acquire e por release sem disputa; um par comum soma duas instruções | Precisa medir impacto nos jogos; não estimar ns ou FPS |
| Retry/pending | ADD sai do loop; callbacks restauram/recalculam endereço, com SUB antes de callback e saída | Contenção e frequência dependem do workload |
| Locks/syscalls/barreiras | Zero novos; LDAXR/STLXR e ordenação existentes mantidos | Não remover o protocolo de sincronização nesta rodada |
| Pilha/registradores | Zero frames ou registradores adicionais | Mantém prologue/epilogue e HostSp existentes |
| Instrumentação | Nenhum log/contador novo no sinal ou hot path | Telemetria R1 preservada; não conta uso destes helpers |
| Guarda do assembler/testes | Trabalho apenas na geração/teste host | Não é otimização de FPS |

É uma correção arquitetural com custo pequeno e explícito, não uma promessa de desempenho. Uma otimização pode ser considerada depois, mantendo operandos válidos. Não misturar com ESR, tracking, NRO, relógios, cache/publicação, RequestInterrupt/Dispose ou filtros já publicados.

## Validação preparada e limitações

Fixture `NceExclusiveProtocolTests`: **34 casos preparados**, com Unicorn executando os helpers ARM64 reais. Cobre quatro mapas de registradores, acquire, releases sem pending/com pending, retry CLREX, requests durante callbacks, espera de pending=2, restauração de contexto/SP/callee-saved e políticas de false. Também rejeita operandos sobrepostos e inspeciona os geradores reais de entrada, suspensão, wrapper, SVC e contador em todos os destinos 0–30.

Callbacks e contexto são sintéticos; não há kernel, sinal real, concorrência real entre núcleos, TLS real ou medição de tempo. O teste de pending=2 modela o serviço existente, não prova entrega pelo scheduler. A inspeção dos geradores não executa lookup/index/TLS. A fixture não comprova o comportamento de instruções inválidas em hardware nem corrige F03. None/signal continuam com a política antiga de ignorar false. F02, F03 e F04 continuam pendentes; o acionamento real da defesa R5 continua não medido.

Codex realizou parsing sintático de três C# sem erros; isso não comprova compilação semântica, NativeAOT ou execução. **Não compilou, executou testes, gerou APK, fez commit/push nem acessou aparelho.** Os checks de hashes/escopos/patches estão em `VERIFICACOES_NCE_R6.json`, depois da aplicação. Antigravity deve executar os 34 casos, R4/R5 e os checks anteriores, compilar e inspecionar proveniência/identidade conforme o prompt desta rodada.

## Validação e reversão

Comparar com R5 aprovada, mesma cena/save/cache/Turnip/limite de FPS/temperatura. Priorizar MK11, Doom, Zeldas, Metroid Prime 4, abrir/fechar/reabrir, suspensão normal, input e áudio. Reprovar por crash/congelamento/encerramento travado, corrupção ou piora repetível de fluidez/FPS. Sem melhoria mensurável não retirar a validade arquitetural, mas investigar custos antes de adotar como base.

Cinco arquivos em dois componentes: EXCLUSIVE (patcher, assembler, testes) e METADATA (versão, este documento). Snapshot anterior em `work/nce-r6/before`; patches completos e separados em outputs. Reversão autorizada futuramente: conferir o reverse-check e restaurar somente esses arquivos da base R5, removendo apenas os dois novos arquivos desta rodada. Nunca reset/checkout geral. Retirar só EXCLUSIVE conserva versão/metadados, portanto ajustar a identificação da build para não a apresentar como R6 funcional. Não executar reversão nesta tarefa.
