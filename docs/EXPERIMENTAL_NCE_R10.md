> **R10 inicial reprovada em 03/10/2026:** três logs mostram FailFast NativeAOT em `.NET TP Worker`; JIT funciona. A correção **0.0.16-nce-r10-fix / 20123** substitui o encaminhamento host/sem contexto abaixo. Esses caminhos seguem diretamente para a cadeia nativa anterior, sem tracking gerenciado, snapshot ou callback NCE. Demais contratos R10 mantidos. O texto original abaixo é histórico e sua afirmação de que tracking host seria legítimo é retirada: estado NCE/pilha de sinal não estabelecem uma transição GC válida. Ver `outputs/DIAGNOSTICO_NCE_R10_FIX.md` e manifesto de correção. Validação Android pendente.

# NCE R10 — transições e falhas de memória no Android

03/10/2026. Escopo autorizado: **concluir F02 (transições nativo/gerenciado) e implementar F04 (classificação/encaminhamento de falhas) na mesma build**. Experimental **0.0.15-nce-r10 / 20122**, `org.kenjinx.android.experimental`, Ryujinx Neo Experimental. R9 está aplicada e continua em validação pelo usuário. Não presumir aprovação prática da R9/R10.

## Base e histórico

Current e Experimental: HEAD `d24e22d2d2714fecdae89377f1261ac49d79e3b1`, branch `neo-v2-nce2`; rodadas locais sem commits. Conferidos os 59 arquivos sobrepostos de R1–R9/QoL1–4 e capturados hashes de todos os 4.432 arquivos rastreados em cada pasta antes de preparar esta rodada. Snapshot em `work/nce-r10/before-state.json` e `before/`. Não usar a retirada R7 cancelada nem patches descartados.

Ponta remota novamente lida por API em 03/10: `06b96f2a7103bffe44a63bea01e0a774e8ce2d7c`. [Changelog](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/CHANGELOG.md), blob `b3b4c60d591eac02720aa5747c919063de60696d`, sem mudança nesta revisão. Evidências `remote-branch.json`/`remote-changelog.json`. Mantêm-se serialização RequestInterrupt/Dispose, filtro/ordenação de exclusivas, relógios, cache, NRO e permissões/tracking. O último commit do patcher na base d24 é `2a1c968f0`; relembrar a comparação histórica de aquisição/troca de SP em `e8e4c5aa61` e FP/snapshots em setembro nos estudos R7–R9, sem importar implementações externas.

Referências relidas: Eden `d16735f5b618942136d6ab53466e3be0a382c30a`, `ArmNce::SaveGuestContext` e `HandleFailedGuestFault`: [fonte](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/arm/nce/arm_nce.cpp#L84). Eden troca a frame/TLS com outro contrato e pode pular uma instrução em falha de dados não tratada; esse comportamento não foi portado. Ryubing local reconfirmado em `475615f0431b4995b03c930c5089d20f6f3570e0`, `MemoryManagerHostMapped.Reprotect/TrackingReprotect`: [fonte](https://git.ryujinx.app/projects/Ryubing/src/commit/475615f0431b4995b03c930c5089d20f6f3570e0/src/Ryujinx.Cpu/Jit/MemoryManagerHostMapped.cs#L410). É JIT/mirror, não o caminho Android NCE; seu Reprotect vazio não é solução para este mapa nativo. Não revalidada uma nova ponta Eden/remota Ryubing.

## F02: contrato completo de transição desta build

Estado conserva os bits publicados: 0 nativo, 1 gerenciado/transição, 2 nativo com interrupção pedida, 3 gerenciado com pedido. Não introduzir um quinto estado que suprima o envio do sinal sem responsável por consumir pending.

| Caminho Android realmente utilizado | Tratamento na base + R10 |
|---|---|
| Entrada inicial | R4 mantém 1 até SP/FP guest preparados e preserva/consome pending; altstack antes da publicação do handle. |
| SVC e CNTPCT | R8 adquire ainda na pilha guest; R9 restaura SP/FP/NZCV antes de publicar 0. As rotinas permanecem exatas. |
| SIGUSR2 entregue em 1/3 | Defesa R5 preservada no gerador Android: OR de pending e retorno na pilha do sinal, sem callback, snapshot ou HostSp. |
| SIGUSR2 em 0/2 com PC host/helper | Callback na pilha do sinal, preservando FP corrente localmente; não publicar SP/PC/NZCV/FP host como guest nem reutilizar HostSp ativo. Também cobre a cauda global da entrada inicial. |
| SIGSEGV guest em 0/2 | Classifica primeiro e faz claim atômico 0→1 ou 2→3 na pilha do sinal; só depois publica snapshot/FP e usa HostSp. |
| Retorno SIGUSR2 e SIGSEGV guest | `WriteAndroidSignalReturn` restaura FP guest e pilha original do sinal antes do STLXR que publica 0. Pending 3→1 retorna explicitamente a HostSp/FP host para o callback; repete a preparação e o serviço de pedidos adicionais. |
| Cauda entre publicação 0 e sigreturn | SIGUSR2 bloqueado pela própria entrega; no SIGSEGV, também pela máscara instalada para o wrapper NCE. O kernel restaura frame e máscara antes de entregar SIGUSR2 pendente. |
| Fault durante chamada/transição gerenciada | Sem tentar adquirir 1/3: dispatcher na pilha do sinal, FP corrente salvo localmente, FP host durante callback e corrente restaurado ao voltar. Estado/snapshot/HostSp ativos preservados. |

**Interação essencial:** bloquear SIGUSR2 no wrapper e conservar o antigo acquire que espera estado 2 produziria deadlock: a thread esperaria um sinal que ela própria bloqueou. Por isso o claim do fault Android preserva 2 como 3 e atende o pedido no retorno. Não usar este claim na entrada SVC/contador, onde a espera atual segue correta e o sinal permanece entregável.

A máscara SIGSEGV NCE inclui somente SIGUSR2 adicional, sem syscall por fault/SVC. A instalação mantém SA_SIGINFO/SA_ONSTACK e não ativa NODEFER no wrapper. O contrato de restauração de máscara e de autodisarm está no [kernel Linux 6.1, signal_delivered](https://github.com/torvalds/linux/blob/v6.1/kernel/signal.c#L2694). Kernel/dispositivo real não foram executados por Codex; esta propriedade exige validação Android.

## F04: classificação e cadeia completa

Produção: `NceCpuContext` seleciona `GenerateAndroidWrapperExceptionHandler` somente em Bionic e `GenerateSuspendExceptionHandler()` seleciona o gerador Android. Os geradores legacy mantêm seu dispatch para outras plataformas. Não forçar IsBionic no host para simular equivalência.

Antes de tocar contexto/pilha: modo 1/3 encaminha ao dispatcher host; modo nativo aceita eventos de memória MAPERR/ACCERR e PC no intervalo real da reserva VA guest. PC fora desse intervalo é origem host/helper mesmo em estado 0/2. A reserva vem de `MemoryManagerNative`, incluindo ilhas dentro do mapa; não é um teste de permissão X nem substitui F08. Intervalo vazio/nulo não autoriza claim guest. Dois campos são anexados ao final de NativeCtxStorage e inicializados no construtor antes de registrar/publicar a thread; offsets existentes preservados.

O caminho host chama tracking legítimo no signal stack com FP host e 16 bytes de estado FP local. Não sobrescreve GuestSp/Pc/Pstate/FP, não limpa pending e não reutiliza a pilha host já ativa. Falta de contexto usa o dispatcher genérico completo, incluindo tracking e cadeia; deixa de saltar diretamente para um ponteiro sem considerar flags/disposição.

O dispatcher Android aceita apenas si_code 1/2 e ESR válido de data abort EC 0x24/0x25. Software SIGSEGV, instruction abort, síndrome ausente/inválida e MTE vão à cadeia anterior. Endereço TBI é normalizado para consulta de faixa, mantendo siginfo/registradores originais. Tracking que retorna zero deixa a frame intacta e não lê PC nem modifica seu registrador de endereço. Retorno positivo conserva a correção de alias existente. A seleção `UnixOldSigaction3Arg` passa a carregar I32, correspondente ao campo real; I64 também lia o primeiro IsActive da tabela adjacente.

`NativeSignalHandler` guarda a sigaction anterior completa, e a instalação NCE substitui seu ponteiro de fallback por `NceSignalChain`, com assinatura de três argumentos. Código nativo gerado:

- SIG_IGN retorna como disposição, sem BR 1; SIG_DFL restaura disposição e redispara SIGSEGV na própria thread, com exit_group como fallback de erro, sem BR 0 ou retorno ao acesso inválido.
- Handler customizado respeita assinatura de um/três argumentos; compõe máscara anterior com uc_sigmask e bloqueio próprio conforme NODEFER; salva/restaura máscara do wrapper por duas syscalls somente nesse caminho não tratado pelo tracking.
- RESETHAND é uma disposição anterior lógica consumida uma vez por claim exclusivo compartilhado. Não remover o handler de tracking ao primeiro fault tratado. A consulta pública de sigaction continua vendo o wrapper instalado, como em qualquer composição de handlers.
- RESTART e EXPOSE_TAGBITS são herdados pela instalação. A pilha/retorno/restorer da entrega pertencem ao wrapper NCE; não criar uma segunda frame de kernel ou copiar offsets de Eden. Alterações na frame realizadas pelo handler anterior sobrevivem.

Uc_sigmask LP64 em 40 confirmado nos [cabeçalhos Bionic](https://android.googlesource.com/platform/bionic/+/1cb31580f850f346fd64fa96e6b998674145c72f/libc/include/sys/ucontext.h#104). Flags/máscara usados conforme [signal_delivered e SA_ONESHOT no Linux](https://github.com/torvalds/linux/blob/v6.1/kernel/signal.c#L2588); syscall IDs e sigsetsize conforme [UAPI ARM64/asm-generic](https://github.com/torvalds/linux/blob/v6.1/include/uapi/asm-generic/unistd.h#L393). `PROBE_NCE_R10_BIONIC.c` verifica offsets/flags/IDs contra o NDK efetivo; ainda não compilado.

## Custos e preservação

Correção funcional, sem ganho de FPS prometido. Claim/classificação acrescenta cerca de 19–26 instruções sobre o acquire antigo, conforme MAPERR/ACCERR e desvios, antes do custo do dispatcher. ESR/si_code/TBI adicionam verificações no caminho de fault, não em cada instrução guest. O retorno de sinal sem pending conserva a ordem de grandeza/contagem dos mesmos restores/atomics; pending repete cerca de 11 instruções de FP/seleção de pilha por callback. São contagens previstas no gerador, não medidas; Antigravity deve conferir o emitido.

Zero locks/syscalls explícitas/barreiras globais novas no caminho de tracking tratado; atomics continuam LDAXR/STLXR, agora com segundo probe na classificação. SIGUSR2 nativo ganha também cerca de 9 instruções de classificação de PC; caso host/helper usa 16 bytes locais para FP e o signal stack, inclusive callbacks pending. Syscalls novas somente no fallback anterior: duas para máscara do handler customizado; até cinco para fatal redispatch, mais exit_group se necessário. Pilha adicional host-fault: 16 bytes; fallback: 64 bytes além dos saves. Dois campos de contexto: 16 bytes por thread; blocos do dispatcher persistem enquanto o handler global existir. Mascaramento pode adiar entrega até terminar um handler: medir latência/stalls, sem declarar custo zero.

Current não é alvo. QoL1–4, botão de fechar que some no primeiro clique e identidade Experimental permanecem. `TextureGroup.cs` mantém SHA256 `9E9720C8891523D5469214C46E7C5960CDD8960F9A79D72056B959F71F439A36` / 339470ed. Sem alteração em GPU/fila/compressão/clock/loader/permissões. Telemetria R1/R2 preservada, sem logging/clock/contador novo no handler.

## Verificação e conclusão por tópico

Três fixtures novas: geradores reais Android em Unicorn (classificação 0/1/2/3, PC/si_code, publicação, perda de reserva, pending durante cauda, repetição, deferral, lookup ausente); dispatcher completo IR Android compilado para x64 (ESR/si_code, tracking, fallback e frame/alias/TBI); cadeia ARM64 real em Unicorn (default/ignore/assinaturas/máscaras/reset/erros). Contagem está em `MANIFESTO_NCE_R10.json`. Syscalls são interceptadas sinteticamente; não executar kill/exit ou enviar sinais reais no host. Fixtures anteriores ficam intactas e continuam obrigatórias.

Codex faz apenas parsing, hashes, diffs/checks e revisão de caminhos. **Não compilou, executou testes/probes, gerou APK, fez commit/push ou acessou aparelho.** Parsing não comprova compilação/semântica. Testes de CPU simulam kernel/reservas/callbacks; IR x64 não comprova ABI NativeAOT/Android, máscaras/altstack reais, scheduler/multicore ou frequências. F02/F04 têm implementação de todo o escopo Android nesta build; só podem ser encerrados como validados após testes/build e validação prática.

Continuam tópicos distintos: F03 (término definitivo por sinal, incluindo false/None), F11 (snapshot completo de thread pausada), F05 (NRO dinâmico), F08 (permissões/tracking). False em callbacks de sinais mantém a política anterior; não inventar um Ret host sem frame válida. Nenhuma destas quatro pendências será apresentada como extensão escondida de F02/F04. Hipóteses M01–M07 continuam condicionadas a evidência/medição.

Validar R10 contra R9 com mesmas cenas/save/cache/driver/limite de FPS/temperatura e sessões repetidas em Doom 2016, MK11, Zeldas e Metroid Prime 4; medir mediana/p99 e resposta a pausa/retomada/fechamento, dez ciclos abrir/fechar/reabrir. Verificar instalação NCE SIGSEGV result=0, mask=0x800 e flags esperadas nos logs R2 existentes. Doom permanece intermitente e sem causa comprovada; não atribuir/certificar correção pelas alterações desta rodada.

Se reprovar, corrigir dentro da R10 ou retirar somente R10 a partir do snapshot R9, com versão posterior adequada para instalação. Preservar R1–R9/QoL e histórico; sem reset, sem reintroduzir retirada R7 cancelada. Não executar reversão automaticamente. A R9 ainda está em validação; se falhar, interromper a validação comparativa e diagnosticar a base antes de promover qualquer tópico.
