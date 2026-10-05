# R5 — adiar suspensão tardia durante execução gerenciada

Experimental **0.0.9-nce-r5 / 20116**, pacote `org.kenjinx.android.experimental`, nome **Ryujinx Neo Experimental**. Base: R1 + QoL1/2/3 + R2 + R3 + R4 aprovadas, sobre HEAD `d24e22d2d2714fecdae89377f1261ac49d79e3b1`, branch `neo-v2-nce2`. A R4 foi aprovada pelo usuário, que relatou melhora de FPS; não há medição quantitativa ou captura do aparelho por Codex.

## Evidência e recorte do estudo

O changelog remoto foi relido antes desta rodada: [branch neo-v2-nce2](https://github.com/Sunshinin11/Ryujinx_Neo/tree/neo-v2-nce2), ponta `923510a9558e41aee36a06d70782f9febfd14a97`; texto do [changelog fixado](https://github.com/Sunshinin11/Ryujinx_Neo/blob/923510a9558e41aee36a06d70782f9febfd14a97/CHANGELOG.md). O blob conhecido dessa mesma ponta é `b3b4c60d591eac02720aa5747c919063de60696d`. Não foi aplicada nenhuma correção descartada nem revertida alteração publicada.

Esta rodada prepara uma proteção defensiva ligada a F02 da auditoria original: `GenerateSuspendExceptionHandler` encontrava o contexto e, sem verificar bit0 de `InManaged`, salvava estado FP/Pstate como guest, selecionava `HostSp`, gravava estado 1 e chamava novamente o callback. **Se** SIGUSR2 encontrar estado gerenciado, `HostSp` pode referenciar uma pilha host já em uso: reentrar a partir dela pode sobrepor frames ativos e salvar estado host como guest.

Ressalva da revisão: `RequestInterruptCore` primeiro faz OR de pending e só envia sinal quando o valor anterior era 0; `WriteInManagedLockAcquire` espera enquanto o estado é 2. Esse protocolo já impede a corrida simplificada “um pedido normal marcou 2, o sinal atrasou e a mesma thread adquiriu 1”. A auditoria original não demonstrou a entrega em estado 1/3 por esse fluxo normal. A guarda também cobre um SIGUSR2 externo/inesperado sobre um contexto gerenciado, mas **não foi identificado um produtor desse sinal no uso normal do Neo**. Classificação desta proposta: proteção funcional defensiva, cuja necessidade/benefício real depende de comprovar o gatilho e medir; não classificar como correção necessária com gatilho normal comprovado. A condição condicional é testável, sua frequência no Thor não foi verificada. Não há atribuição aos problemas descartados nem às oscilações da R2.

Histórico lido: o handler foi introduzido em `e8e4c5aa61` (10/05/2025); a fundação Neo de setembro acrescentou preservação FP/SP/Pstate em `f24d6f7068`. Esses comportamentos permanecem no caminho nativo. A implementação foi derivada do código e do protocolo local, sem importar patches de Eden/Ryubing ou assumir equivalência de plataformas.

O caminho efetivo Android é: `RequestInterruptCore` marca pending com OR atômico → para valor anterior 0, `NceThreadPal.SuspendThread` seleciona `NceThreadPalAndroid.SuspendThread`/`pthread_kill` → `NceCpuContext` publica o código de `GenerateSuspendExceptionHandler` e o registra por `NativeSignalHandler.InstallUnixSignalHandler` → lookup seguro existente resolve o contexto. Depois de conferir o número do sinal, R5 verifica o modo **antes** de ler Pstate do ucontext, tocar o snapshot FP ou mudar SP. A infraestrutura Bionic R2 e a publicação/lifetime existentes continuam iguais. Fontes de referência: [contexto d24e22](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NceExecutionContext.cs), [patcher d24e22](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NcePatcher.cs). A fonte local aprovada acrescenta as R1–R4; esses links não substituem os hashes locais.

## Alteração funcional

| InManaged observado | Significado existente | Ação R5 |
|---|---|---|
| 0 | Native, sem pedido | CLREX e mesmo caminho nativo anterior |
| 2 | Native, pending | CLREX e mesmo caminho nativo anterior |
| 1 | Managed, sem pending | LDAXR/STLXR publica 3; retorna pela pilha corrente do sinal |
| 3 | Managed, pending | Mantém 3 atomicamente; retorna pela pilha corrente do sinal |

No caminho gerenciado, endereço, valor e status do STLXR usam registradores distintos. Falha da reserva repete a leitura/decisão. O outro core pode marcar o bit pending, mas a thread proprietária está interrompida e não pode executar sua liberação simultaneamente. O retorno usa o epílogo já existente; não chama HLE, logger, allocator ou callback gerenciado e não usa HostSp. O kernel restaura o contexto interrompido no retorno de sinal; o atendimento fica com os caminhos de liberação já existentes, incluindo a entrada inicial da R4. Isso pode adiar o atendimento até o fim do trecho host; não é um mecanismo novo de preempção do código gerenciado.

Nos estados nativos o corpo anterior permanece idêntico. O CLREX descarta a reserva usada na classificação. A sobrecarga interna com contexto/callback sintéticos é selecionada **na geração** para testes; em produção, ponteiro zero conserva o lookup de tabela/índice, sem branch adicional emitido para escolher contexto de teste.

## Custos e limites

Correção funcional, sem promessa de ganho de FPS. A classificação adiciona aproximadamente **5 instruções ARM64** no caminho nativo por sinal válido, incluindo um LDAXR e CLREX. A deferral tem aproximadamente **8 instruções**, incluindo LDAXR/STLXR, antes do epílogo existente; cada reserva perdida repete aproximadamente 6. Contagem de instruções não é estimativa de tempo: cache/coerência e frequência de sinais devem ser medidos contra R4 no Thor. Acrescenta ordenação acquire/release por sinal, sem lock, syscall, DMB/DSB/ISB explícito ou trabalho extra por frame/SVC/tick/fault. Não há logging ou novo contador no handler.

Telemetria R1 e logs ABI R2 foram preservados. O marcador R1 permanece congelado; a identificação desta rodada exige versão, hash do APK e proveniência da biblioteca. Contadores existentes registram solicitações/atendimentos, mas **não distinguem sinal adiado** nem comprovam frequência dessa corrida.

**Não resolve F02 inteiro:** há janelas em que a pilha já mudou e bit0 ainda é zero, que exigem protocolo de transição próprio. Não resolve F04 (classificação de faults host e encadeamento seguro de handler anterior) nem F03 (callback stop no handler de suspensão ainda tem TODO e precisa saída segura do frame do kernel). Não altera máscaras, sigaction, altstack, lifetime, clocks, cache, tracking, NRO, wrappers, exclusivas ou branches. Código NCE compartilhado pode afetar Linux/macOS ARM64; não foi executado nessas plataformas. Não enviar sinais artificiais ao jogo para tentar reproduzir.

## Testes e preservação

`NceSuspendDeferralTests` prepara **17 casos** com o gerador real do handler em Unicorn: estados 1/3; contexto/registradores/vetores/SP/FP preservados; HostSp/ucontext inválidos sem acesso; sinal incorreto nos quatro estados; estados nativos 0/2 com callback; invalidação de reserva por pedido concorrente e por CLREX; repetição de entregas; canário na pilha host ativa; pedido produzido pelo handler consumido pela entrada real da R4 antes de loop guest sem SVC.

O teste usa contexto e callback sintéticos. Não instala sinais, não executa NCE no host, não cobre o lookup real de TLS/tabela, entregas do kernel, scheduler, simultaneidade de núcleos reais ou tempo de atendimento. Invalidação explícita por CLREX evita presumir que escrever pela API Unicorn invalida uma reserva. No host Windows a ramificação Linux/Bionic de Pstate do corpo nativo não é emitida; sua preservação é conferida no diff e requer build/validação Android.

Codex fez parsing C# sem compilação e verificação dos hashes/escopos/patches. **Não compilou, não executou os testes, não gerou APK, não fez commit/push e não acessou o aparelho.** Os testes novos e anteriores ficam obrigatórios no prompt Antigravity. A R4, a interface de fechamento, QoL, R1, ABI R2 e ESR R3 permanecem preservadas; Current não recebe alterações.

## Validação e reversão

Comparar contra **R4 aprovada**, mantendo driver, cena/save, cache, configurações e temperatura inicial. Priorizar MK11, Doom, Zeldas e Metroid Prime 4; registrar travamentos, áudio/input, carregar save, menus, pausa/retomada e 10 ciclos abrir/fechar/reabrir. Botão de sair deve continuar sumindo no primeiro clique. Medir FPS e estabilidade dos quadros sem atribuir oscilações anteriores automaticamente à R5.

Retirar somente a deferral se houver crash, interrupção perdida, congelamento, fechamento que não termina ou piora repetível exclusiva contra R4. Patches separados: `PATCH_NCE_R5_DEFERRAL.patch` (handler + fixture), `PATCH_NCE_R5_METADATA.patch` (versão + este documento), e patch completo. Antes de reversão conferir hashes e `git apply --reverse --check`; não usar reset a HEAD e não remover alterações aprovadas. Um APK futuro após retirada precisa código de versão maior para atualização normal, mesmo contendo o comportamento R4.
