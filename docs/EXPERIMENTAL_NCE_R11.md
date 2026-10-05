# R11 — Encerramento por sinal no Android NCE

Base: R10 fix 0.0.16-nce-r10-fix / 20123, aprovada pelo usuário após a correção do crash NCE. R11: 0.0.17-nce-r11 / 20124. HEAD local Experimental e Current: d24e22d2d2714fecdae89377f1261ac49d79e3b1, branch neo-v2-nce2, com mudanças locais. O snapshot desta rodada registra os 66 arquivos aprovados e os 4432 arquivos rastreados de cada pasta. A R10 original 0.0.15 / 20122 permanece reprovada e não deve ser restaurada.

## Evidência e caminho afetado

`KThread` solicita interrupção ao terminar uma thread em execução; `Exit` chama `Context.StopRunning()`. `NceExecutionContext.OnInterrupt()` retorna `Running`. A entrada inicial, SVC e contador já respeitam o retorno false. No Android, `GenerateAndroidSuspendExceptionHandler` e o callback pending de `WriteAndroidSignalReturn` descartavam esse retorno e publicavam modo nativo novamente. Logo, uma thread guest em loop sem SVC podia receber a solicitação de término e continuar. Isso é uma lacuna funcional demonstrável no código, não uma causa comprovada de FPS oscilante ou das texturas do Doom.

O histórico/changelog Neo foi relido pela API nesta rodada: [CHANGELOG na branch neo-v2-nce2](https://github.com/Sunshinin11/Ryujinx_Neo/blob/neo-v2-nce2/CHANGELOG.md). Evidência fixada do estudo anterior: commit 06b96f2a7103bffe44a63bea01e0a774e8ce2d7c, blob do changelog b3b4c60d591eac02720aa5747c919063de60696d. A leitura da branch nesta rodada não forneceu SHA de ponta; não afirmamos ter confirmado uma ponta nova. Nenhuma mudança do changelog foi revertida ou importada por equivalência com outro emulador.

Referência Eden: snapshot de auditoria no commit d16735f5b618942136d6ab53466e3be0a382c30a, `src/core/arm/nce/arm_nce.cpp`, `ArmNce::SaveGuestContext`, linha 82. A função modifica a frame do sinal para restaurar contexto host. Não copiamos seu layout, TLS ou política de falhas: Neo tem uma pilha host salva e um epílogo próprio. O snapshot não confirma a ponta atual do Eden. [Arquivo no commit](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/arm/nce/arm_nce.cpp#L82).

Referência Ryubing: HEAD local 475615f0431b4995b03c930c5089d20f6f3570e0, `src/Ryujinx.Cpu/Jit/JitExecutionContext.cs`, `RequestInterrupt` / `StopRunning`. Esse caminho delega ao contexto JIT e não estabelece um contrato de signal-return para NCE Android. [Arquivo no commit](https://git.ryujinx.app/projects/Ryubing/src/commit/475615f0431b4995b03c930c5089d20f6f3570e0/src/Ryujinx.Cpu/Jit/JitExecutionContext.cs).

No Linux ARM64, `restore_sigframe` restaura máscara, registradores e FP; `rt_sigreturn` também restaura a altstack. Isso sustenta retornar ao restorer antes de executar o epílogo host. [Kernel v6.1, signal.c](https://github.com/torvalds/linux/blob/v6.1/arch/arm64/kernel/signal.c#L592), [sigcontext UAPI](https://github.com/torvalds/linux/blob/v6.1/arch/arm64/include/uapi/asm/sigcontext.h). Esses fontes não comprovam a configuração do kernel do Thor, nem a integração NativeAOT em runtime.

## Implementação completa de F03 Android nesta build

1. A entrada captura X18 host antes da entrega dos registradores guest. Acrescentam-se `HostX18` e `SignalExitPc` ao final do contexto, preservando todos os offsets anteriores. O endereço de saída é configurado antes de publicar a thread.
2. O bloco global RX contém um epílogo Android de saída. Ele restaura X18, FPCR/FPSR host, X19–X30 e D8–D15 usando a mesma área de salvamento da entrada e retorna à chamada host original. Não contém callback gerenciado, lookup TLS ou syscall.
3. SIGUSR2 verifica false tanto no primeiro callback quanto nos callbacks pending, para PC guest e PC helper. O retorno de SIGSEGV também respeita false do callback pending. O callback de tracking continua tendo contrato void: não se inventa retorno bool para ele.
4. Na saída terminal, o handler altera apenas SP, PC, X0 e X18 da frame kernel. SP aponta para a área host salva; X0 leva o contexto; X18 é host; PC aponta para o epílogo. Máscara, altstack, PSTATE, FPSIMD, demais GPRs e registros opcionais permanecem intactos. PC é escrito por último.
5. O handler volta à pilha original de sinais e retorna ao restorer. O kernel executa sigreturn antes do epílogo host. Não fazemos RET direto da pilha do handler para o host. O estado permanece 1/3 até a retirada do handle e o cleanup existente de `Execute`; não há publicação terminal de estado 0. Sinal tardio nesse intervalo segue a deferral aprovada, sem novo callback gerenciado.

O encaminhamento nativo da R10 fix para falhas host/sem contexto permanece. Não se entra no tracking gerenciado por essas rotas. `RequestInterrupt`, `Dispose`, `StopRunning`, lookup/index, altstack e `Execute`/finally mantêm o comportamento aprovado. As saídas SVC/contador/entrada e os geradores legados fora de Android permanecem. Esta build cobre F03 para a plataforma solicitada; não altera encerramento NCE Linux/macOS nem pretende resolver snapshot completo de thread pausada.

## Custos e classificação

Correção funcional necessária com evidência de código. Não é uma otimização de FPS. Uma store adicional de X18 por entrada nativa; 16 bytes extras no contexto ARM64. Nos sinais, salvar X23 acrescenta uma store e uma load ao prologue/epilogue, mais uma MOV para carregar a frame; área de salvamento cresce de 48 para 64 bytes, ou 64 para 80 bytes no caso helper com FP local. SIGUSR2 acrescenta um teste do retorno; cada callback pending acrescenta outro. Nenhum custo por instrução guest ou por SVC/contador normal.

Somente ao terminar: quatro stores de 64 bits na frame e suas cargas de valores, CLREX, restauração da pilha/FP do handler e epílogo host de aproximadamente 16 instruções. Não há locks, syscalls, barreiras ou pausas novas. O sigreturn já existia ao sair do handler. O custo real de sinais/callbacks e o efeito no Thor dependem de medição; não se atribuem nanossegundos por estimativa estática.

Instrumentação: marcador único de inicialização `[NEO-NCE-R11] signalStop=kernel-sigreturn-host-epilogue hostX18=saved-at-entry`, fora dos handlers, mais telemetria aprovada anterior. Nenhum log, contador gerenciado ou alocação foi colocado no sinal ou epílogo.

## Verificação, validação e reversão

16 casos novos em `NceSignalTerminationTests` executam, quando rodados, entrada/handlers/epílogo Android reais em Unicorn com entrega/restauração de frame simuladas: loop sem SVC, estados 0/2, PC helper, primeiro/segundo callback false, pending após fault, retorno true, sinais tardios nos estados 1/3, pedido durante preparação terminal, preservação de frame inteira e restauração ABI host. Os 41 casos de `NceAndroidSignalTests` continuam, com expectativas terminal/stack ajustadas. As fixtures R10 de dispatcher e cadeia nativa não são modificadas.

Codex verificou somente sintaxe, diff, hashes e invariantes por leitura. Testes, probes, compilação, APK e validação real são tarefas do Antigravity/usuário. Simulação não prova máscara/altstack reais, scheduler/multicore, NativeAOT GC/TLS, SVE/SME, ABI de callbacks ou janela real entre sigreturn e retorno host. Não declarar F03 validada no Thor antes desses resultados.

Validar primeiro abertura NCE de vários jogos. Depois fechar/reabrir 10 vezes, fechar durante carregamento e gameplay, alternar jogos e NCE/JIT, pausar/retomar e colocar em background. Nenhuma tela branca permanente, crash ou thread guest persistente; confirmar disponibilidade de reabrir e consumo de CPU após encerrar. Comparar Doom, MK11, Zeldas e Metroid Prime 4 com R10 fix nas mesmas condições. As texturas/fila/GPU não foram alteradas; o defeito intermitente anterior do Doom deve ser registrado separadamente.

Reversão deve substituir apenas os seis arquivos existentes pelos backups desta R11 e retirar apenas os dois arquivos novos, após confirmar que não receberam mudanças posteriores. Não usar reset de repositório, rollback R6/R9 ou aplicação da R10 inicial. `work/nce-r11/before` guarda a base R10 fix. Nenhuma promoção à Current nesta etapa.

Após F03, restam três frentes do plano: contexto completo de thread pausada, NRO dinâmico e composição de permissões/tracking; depois validação final. A R11 não reabre os dois tópicos da R10 aprovada.
