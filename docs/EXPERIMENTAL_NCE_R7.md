# NCE R7 — adquirir ownership antes da transição do wrapper de falha

Data: 2026-10-02. R6 aprovada expressamente pelo usuário: “ta tudo ok na R6”. Isso registra validação prática, sem inventar resultados quantitativos. Base preservada: R1 + QoL1/2/3 + R2/R3/R4/R5/R6 + QoL4. Versão **0.0.12-nce-r7 / 20119**, `org.kenjinx.android.experimental`, Ryujinx Neo Experimental. Current não recebe alterações.

## Estado e evidências

Current e Experimental continuam na branch `neo-v2-nce2`, HEAD `d24e22d2d2714fecdae89377f1261ac49d79e3b1`. As rodadas aprovadas são alterações locais, sem commits. Os 53 arquivos do conjunto aprovado foram conferidos por SHA-256 sobreposto antes desta rodada. Comparação de todos os 4.432 arquivos rastreados por repositório e escopos está no relatório de verificações. Não restaurar HEAD ou reaplicar rodadas descartadas.

O [changelog publicado](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/CHANGELOG.md) foi relido na ponta remota `06b96f2a7103bffe44a63bea01e0a774e8ce2d7c`; blob `b3b4c60d591eac02720aa5747c919063de60696d` permanece igual. Seu texto ainda descreve a base d24, não substitui os manifestos locais. O trabalho NCZ dessa ponta foi comparado na QoL4; não importar seu reader/libzstd nem o commit inteiro nesta rodada.

Foi relida a implementação atual e a histórica de [NcePatcher.cs em d24](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NcePatcher.cs). Blame atribui o acquire após a troca de SP a `e8e4c5aa61` (10/05/2025); snapshots/FP entraram em setembro (`f24d6f7068`, `d29a1ebf53`). A [PR6](https://github.com/Sunshinin11/Ryujinx_Neo/pull/6/files), ponta `f9a140b54731ceabf8fafd16add48d325c45b1cc`, também propõe acquire antes de HostSp. Sua versão não tem os mesmos snapshots/FP da nossa base. A R7 adapta somente essa propriedade ao código atual; nenhuma outra alteração da PR foi importada. Evidência API preservada em `work/nce-r7/r7_review_*.json`.

Eden e Ryubing permanecem referências do estudo original, não fonte de novos patches: Eden `d16735f5b618942136d6ab53466e3be0a382c30a` tem outro contrato de TLS/contexto/retorno de sinal; Ryubing local `efbcc01d8b4553c1ae7f0b485c7e3fa249c3b21f` usa JIT/mirror. Não se revalidaram pontas novas desses projetos nesta rodada. Não afirmar equivalência com Android nem portar locks/assembly deles.

## Caminho atingido e condição concreta

Android/Bionic instala este wrapper por `NceCpuContext` → `NativeSignalHandler.InstallUnixSignalWrapper` → `GenerateWrapperExceptionHandler`. `UnixSignalHandlerRegistration.RegisterExceptionHandler` usa `SA_SIGINFO | SA_ONSTACK` e máscara vazia; a instalação alternativa usa `SS_AUTODISARM`. `NceExecutionContext.RequestInterruptCore` faz OR de pending e só envia SIGUSR2 quando o estado anterior é 0; `NceThreadPal.SuspendThread` seleciona `NceThreadPalAndroid`/pthread_kill no Bionic. SIGUSR2 é outro sinal e não é bloqueado automaticamente por SIGSEGV. As máscaras, ABI e lifetime publicados em setembro são preservados.

Antes: lookup → snapshot SP/PC/Pstate → guardar FP guest/carregar FP host → trocar para HostSp → acquire 0→1 → callback. Entre usar HostSp e adquirir, outro pedido pode observar 0, trocar para 2 e enviar SIGUSR2. O handler de suspensão recebe 2 e usa o mesmo HostSp. Com altstack autodisarmed e SP já host, pode haver reutilização de pilha/frame ativo. O código admite a sequência; sua frequência e ocorrência concreta no Thor **não foram medidas**. Não atribuir a ela crashes ou oscilações antigas.

Depois: lookup → **acquire na pilha do sinal** → snapshot original → FP → HostSp → callback. Se houver 2, o acquire existente aguarda naquela pilha até o handler existente atender o pedido; HostSp ainda não foi ocupado por este wrapper. Se claim vencer, o estado fica 1, RequestInterrupt passa a marcar 3 sem novo envio; uma entrega já pendente encontra 1/3 e a guarda R5 defere sem trocar SP ou ler ucontext. O release R6 atende pending conforme a política já existente. O snapshot ocorre depois, lendo a frame original, e evita deixar Pstate do sinal aninhado como snapshot final da falha.

R7 não acrescenta aquisição: move a já existente. Mantém os quatro estados, registradores scratch, prologue/epilogue, fallback ao handler anterior quando lookup falha, callbacks, offsets de plataforma e ordenação LDAXR/STLXR. R6 restaura ctx à base antes das operações seguintes. A política `ThreadExitMethod.None` permanece; não transformar false/stop em um retorno inventado.

Esta evidência é diferente da hipótese anterior de uma solicitação comum atendida depois de acquire sem pending: o protocolo já impede essa sequência. Não repetir a hipótese como diagnóstico comprovado. O alvo aqui é o uso de HostSp **antes** do claim.

## Classificação e custos

| Item | Classificação | Custos/limites |
|---|---|---|
| Entrada do wrapper antes de HostSp/FP/snapshot | Correção funcional necessária com evidência de ordem/invariante | Reposiciona o mesmo helper: zero instruções ARM64 adicionais no caminho de produção, zero locks/syscalls/barreiras/frames/registradores novos. Tempo/contensão reais não medidos. |
| Pending=2/retry | Já existe; preservado | Espera passa a ocorrer na pilha do sinal. Pode esperar pelo serviço de suspensão; não adiciona pausa, timeout ou syscall. Não prova entrega pelo kernel. |
| Ganho de FPS ou redução de stuttering | Depende de medição | Não é objetivo nem resultado demonstrado da R7. |
| Telemetria | Instrumentação existente preservada | R1 permanece exata; sem logging ou contadores novos no sinal. Não mede esta janela diretamente. |
| Copiar TLS/retorno Eden ou JIT Ryubing | Não aplicável diretamente | Contratos distintos; nenhuma mudança. |

O acquire fica com bit gerenciado ativo por um trecho maior (snapshot/FP/troca SP). Pedidos nesse trecho são atendidos pelo release existente; isso muda o momento da suspensão, mesmo sem adicionar instruções. Medir resposta a pausa/encerramento e estabilidade; não alegar custo de latência zero.

## Testes preparados e o que falta verificar

`NceFaultTransitionTests` contém **10 casos preparados**. Unicorn executa o wrapper ARM64 real com seleção de contexto/callback na geração; o overload de produção com dois parâmetros mantém lookup e ponteiros originais. O seam não instala sinais, não muda dispatch de plataforma e não gera branch de teste em runtime.

Casos: aquisição antes de mutação de contexto/FP e HostSp; pending=2; store exclusivo falho com CLREX; request entre load/claim com invalidação explícita; pending consumido pelo release R6 com callback true/false preservando None; quatro entregas aninhadas executando o handler R5 real, em 1/3 e antes/depois da troca de pilha. Verificam args do callback, ctx base, FP host/guest, SP, callee-saved, contexto e área da pilha host. Callbacks podem sobrescrever GPR caller-saved; a restauração final desses GPR/V cabe ao kernel, não se exige do wrapper o que seu contrato não faz.

A frame e a restauração de registradores/NZCV na entrega aninhada são **sintéticas**. Pending=2 modela conclusão do handler, sem thread concorrente. Nenhum teste prova kernel/scheduler, altstack real, pthread_kill, masks, TLS/index real, NativeAOT ou frequência da janela. Snapshot Linux/Bionic só é emitido no ramo real correspondente: no Windows os testes verificam ownership/SP/FP, mas **não executam** leitura do ucontext Linux. O dispatch de plataforma não foi forçado para esconder essa limitação. Darwin não foi validado. Fault em origem gerenciada (1/3) continua assunto de F04, sem teste que o apresente como resolvido.

Codex apenas verifica sintaxe, hashes, escopos e patches. **Não compilou, executou testes/probes, gerou APK, fez commit/push ou acessou aparelho.** Parsing não confirma semântica. Antigravity deve executar a nova fixture e os testes preservados e construir a biblioteca nova/APK conforme o prompt.

F02 permanece **parcial**: SVC/contador em `WriteManagedCall` ainda trocam SP antes de adquirir e liberam antes de restaurar FP/SP; o retorno do wrapper também libera antes da restauração. F04 (origem host/guest), F03 (saída de sinais/false), F11 (snapshot completo), F05 (NRO) e F08 (tracking) continuam separados. Não reclassificar tudo como concluído por aprovação de R7.

## Validação e reversão

Comparar com R6 aprovada/QoL4, mantendo save/cena/cache/Turnip/limite de FPS e temperatura comparáveis. Priorizar MK11, Doom, Zeldas e Metroid Prime 4; observar congelamentos, crash, áudio/input parado, resposta a pausa e fechar/reabrir. Confirmar botão de fechar sumindo no primeiro clique e toda QoL. Sem sinais artificiais ou limpeza global de cache. Reprovar por falha nova reproduzível ou piora consistente de fluidez/latência; registrar cena/logs/tempo, não só FPS instantâneo.

Quatro arquivos: TRANSITION (patcher/testes), METADATA (versão/documento). Snapshot antes de R7 em `work/nce-r7/before`, com 53 arquivos aprovados; manifestos e patches separados em outputs. Reversão futura somente após conferir reverse-check e hashes: restaurar patcher/Gradle dessa base e retirar apenas os dois arquivos novos R7. Preservar **R6 e QoL4**, não reset/checkout para d24. Metadados devem identificar corretamente qualquer retirada parcial. Nenhuma reversão foi executada.
