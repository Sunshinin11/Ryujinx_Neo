# Experimental R4 — interrupção inicial e saída pelo botão

02/10/2026. Android **0.0.8-nce-r4**, versionCode **20115**, label **Ryujinx Neo Experimental**, application ID `org.kenjinx.android.experimental`. Base integral: R1 + QoL1/2/3 + R2 + R3, sem commits locais. Current permanece intacto.

## Escopo e evidência

O changelog remoto foi reconferido em `923510a9558e41aee36a06d70782f9febfd14a97`, blob `b3b4c60d591eac02720aa5747c919063de60696d`, com código publicado até `d24e22d2d2714fecdae89377f1261ac49d79e3b1`. As soluções existentes são a base, inclusive serialização RequestInterrupt/Dispose, filtro/ordenação de exclusivas, snapshots/FP, contexto/index/TLS, cache, PROT1 opcional, GPU/Turnip, VP9, controles, mods, conversões e idioma por jogo. Esta rodada não reaplica experimentos descartados.

**F01 do estudo:** o contexto começa com `InManaged=1`. Depois da publicação do handle, `RequestInterruptCore` pode fazer `1 -> 3`, sem enviar sinal. A entrada antiga fazia `STR WZR`, apagando o pedido antes de começar o guest. Um loop sem SVC poderia continuar sem atender esse pedido. O contraexemplo decorre do código; não foi reproduzido no Thor nesta etapa. A correção é funcional, sem ganho de FPS prometido.

`NcePatcher.GenerateThreadStartCode` agora libera o estado com LDAXR/STLXR no ponto tardio de entrada, já sobre o SP guest. Um OR concorrente faz a tentativa falhar e ser repetida. Estado 3 é consumido atomicamente para 1; somente então se restaura SP/FP do host e se chama o callback de interrupção existente. O contexto é mantido em X19 durante a chamada e o FP host é restabelecido. Ao continuar, recarregam-se os registradores guest e o estado é conferido novamente; ao parar, retorna-se pelo prólogo/epílogo host existente sem executar a primeira instrução guest. NZCV é restaurado **antes** do store que publica estado zero: assim, um sinal imediatamente após esse store não captura flags dos CMP da liberação. Depois disso, X2/X18/X30 e X0/X1 são restaurados sem modificar flags. X18 mantém o valor host até a entrega inicial, inclusive durante o novo callback; os demais trampolines não são reescritos.

`NceExecutionContext.SetStartAddress` instala a altstack antes de publicar o handle. PC/endereço, tamanho de 128 KiB, SS_AUTODISARM, verificações de instalação e desinstalação continuam iguais. Se a instalação falhar, o handle ainda não foi publicado. RequestInterrupt/Dispose mantêm exatamente o lock e as guardas publicados em 28/09; nenhum novo lock é colocado ali.

O código de entrada é compartilhado por NCE ARM64. Android é o alvo da rodada; Linux/macOS ARM64 com NCE também recebem o gerador alterado e não foram executados aqui. As janelas de sinal tardio, transições dos outros trampolines e encerramento dentro de signal handler (F02/F03) continuam pendentes: R4 não declara resolver esses problemas.

## Botão de saída

A proteção visual adicional lembrada pelo usuário se perdeu na volta à Current. O patch descartado não está disponível; esta é uma implementação nova sobre a base aprovada. As guardas NEOV2-CLOSE1/CLOSE2 e a proteção nativa publicada continuam existentes.

`MainViewModel.requestGameExit()` é o único ponto de aceitação UI para o diálogo e o atalho. No primeiro pedido válido, marca `gameExitRequested` antes de qualquer encerramento. `GameOverlay` remove o diálogo e o drawer, impede reabertura por gesto e absorve Back enquanto o pedido está em andamento. O segundo clique, inclusive já enfileirado, é recusado pela mesma marca. O sinalizador não é limpo por `onEmulationStopped`; somente `navigateToGame` começa uma nova geração e libera o botão para a próxima sessão.

Dois callbacks de animação deixam uma oportunidade de desenhar a remoção antes dos joins que já existem em `closeGame`. O teardown continua na thread UI, como antes; não houve migração para uma thread nova. A geração e a rota são conferidas no callback atrasado. Apenas o encerramento aceito executa o fluxo existente e pode remover a rota `game` da pilha; a biblioteca não deve ser removida. `closeGame`, `GameHost.close` e a rotina C# de fechamento mantêm seus corpos existentes.

Cancelar o diálogo antes de confirmar continua funcionando. Ao confirmar, as ações de saída somem; não é preciso esperar o jogo parar para que o botão desapareça. Não há debounce que libere um novo clique depois de um prazo fixo. Exceção durante o teardown é registrada e mantém o pedido bloqueado; a política de recuperação desse erro não foi ampliada nesta rodada.

## Custos e telemetria

F01 adiciona uma tentativa de load/store exclusivo com ordenação acquire/release, comparações e alguns loads na **entrada de cada thread**, com novas tentativas se houver concorrência. Sem pedido pendente não há callback adicional. Com pedido, há uma chamada ao callback existente por pedido consumido, que pode aguardar o scheduler, e recarga de estado para continuar. Não há novos locks, syscalls, logs ou contadores por instrução guest, SVC, leitura de relógio ou fault. A syscall de altstack já existia e foi apenas reordenada. Não foram acrescentadas DMB/DSB/ISB explícitas.

A UI adiciona dois callbacks apenas ao sair, com latência nominal de cerca de 1–2 intervalos de tela antes de começar o teardown, dependendo do ponto do clique e da carga UI. A 60 Hz, aproximadamente 17–33 ms sem atraso adicional; não é prazo garantido. Não acrescenta pausas durante execução normal nem muda gráficos, áudio, clocks ou limitadores. A remoção real do botão antes do bloqueio exige validação no aparelho.

Telemetria R1 e logs R2 permanecem intactos; novos atendimentos iniciais passam pelo `OnInterrupt` existente e entram nos seus contadores. Não foi acrescentado um contador específico que prove F01 em produção. `NEO_EXPERIMENT_ID`/NCE_BUILD continua com o marcador R1 congelado; identificar esta rodada pela versão, manifesto e proveniência da biblioteca, sem confundir o marcador antigo com versão R4.

Relato do usuário: R1 parecia mais com Current; diferenças de fluidez/oscilação começaram a ser percebidas na R2. Zelda LA pareceu mais fluido com FPS semelhante; MK11 oscilou entre 30–60 e Doom entre 25–30 mais frequentemente, com maior fluidez percebida no alvo. São observações subjetivas, sem frame times/controle térmico ou causa comprovada. R4 preserva R2 e R3 por autorização explícita e não pretende corrigir essa oscilação. Comparação principal desta rodada: R3 versus R4; uma investigação causal da R2 permanece separada.

## Testes e limites

`NceThreadStartTests` prepara **12 casos** que executam as instruções ARM64 reais geradas em Unicorn, com callback sintético, sem instalar sinais, abrir NCE real ou chamar libc Android no host. Cobrem entrada normal, pedido inicial com continuidade/parada, pedido antes/depois de LDAXR, falha de reserva/claim, segundo pedido no callback, recarga dos registradores/vetores, NZCV/FPCR/FPSR, snapshot de flags exatamente na publicação de estado zero, SP/FP host e X18. A entrada guest é um loop sem SVC. Um orçamento de 2000 instruções limita loops. A concorrência entre cores é simulada por alteração do estado e CLREX explícito; isso não reproduz o kernel ou a implementação real de exclusivas no Thor.

Codex verificou parsing C# e preservação/diffs das fontes. Não compilou, executou NUnit/Unicorn/Kotlin, gerou APK, fez commit/push ou tocou no aparelho. Compilação semântica/NativeAOT, execução dos testes, integração do scheduler/sinais, comportamento visual/tempo de fechamento, outros hosts ARM64 e desempenho continuam a validar pelo Antigravity e usuário.

Referências: estudo `AUDITORIA_NCE_NEO_2026-10-02.md`, seção F01, entregue nos outputs da conversa; [changelog pinado](https://github.com/Sunshinin11/Ryujinx_Neo/blob/923510a9558e41aee36a06d70782f9febfd14a97/CHANGELOG.md); [AAPCS64, Arm](https://github.com/ARM-software/abi-aa/blob/main/aapcs64/aapcs64.rst) (X18 e registradores preservados); [Android ARM64 ABI](https://developer.android.com/ndk/guides/abis#arm64-v8a).
