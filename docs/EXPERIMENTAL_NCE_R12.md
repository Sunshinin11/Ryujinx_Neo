# R12 — Contexto completo da thread pausada no Android NCE

03/10/2026. F11 implementada nesta única rodada: captura de todos os campos consumidos por GetThreadContext3, pausa reconhecida, trampolines e retomada preservada. Versão 0.0.18-nce-r12 / 20125, Ryujinx Neo Experimental, org.kenjinx.android.experimental. Base R11 0.0.17-nce-r11 / 20124 preservada; o usuário autorizou iniciar R12 enquanto valida R11, sem confirmar aprovação prática da R11. R10 fix aprovada; R10 inicial reprovada não pode voltar.

## Evidência, histórico e plataforma

HEAD local d24e22d2d2714fecdae89377f1261ac49d79e3b1, neo-v2-nce2, fontes locais sobrepostas pelas rodadas. O changelog da branch foi relido via API nesta rodada e preservado em work/nce-r12/changelog-reference.txt. A leitura retornou conteúdo, sem SHA da ponta; a referência fixada anterior é 06b96f2a7103bffe44a63bea01e0a774e8ce2d7c / blob b3b4c60d591eac02720aa5747c919063de60696d. Não houve importação/reset/reaplicação de patches descartados. [Changelog](https://github.com/Sunshinin11/Ryujinx_Neo/blob/neo-v2-nce2/CHANGELOG.md).

KThread.SetActivity pausa e espera a saída dos cores emulados; GetThreadContext3 lê GetCurrentContext sob activity lock e critical section. O getter antigo consulta NceExecutionContext.GetX/GetV e storage. SVC conserva boa parte dos valores na pilha; o sinal conserva registradores na frame kernel. Assim X10/V20/PC/SP consultados podem ser antigos, apesar de a retomada estar correta. O SP salvo pelo SVC é a base do save area, não o SP arquitetural. [KThread no baseline](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.HLE/HOS/Kernel/Threading/KThread.cs#L634).

Histórico relido: d29a1ebf5346d7c5e7a99804887e06c4836dc1bc remove a cópia geral de GPR do fault comum; 2a1c968f0 mantém filtro/exclusivas. A R12 não desfaz isso: registra a origem viva e materializa o estado completo apenas na consulta. A serialização RequestInterrupt/Dispose e as soluções R4–R11 permanecem.

Eden, snapshot d16735f5b618942136d6ab53466e3be0a382c30a: ArmNce::SaveGuestContext copia frame e ArmNce::GetContext entrega o estado ao kernel emulado. O contrato de saída/TLS é diferente; não foi copiado. [SaveGuestContext](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/arm/nce/arm_nce.cpp#L82), [GetContext](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/arm/nce/arm_nce.cpp#L340). Não reconfirmada uma ponta nova Eden.

Ryubing local reconfirmado em 475615f0431b4995b03c930c5089d20f6f3570e0: KThread.GetCurrentContext lê o contexto JIT e tem cache temporal. Não demonstra captura NCE Android; cache não corrige fonte antiga e não foi portado. [Arquivo na revisão](https://git.ryujinx.app/projects/Ryubing/src/commit/475615f0431b4995b03c930c5089d20f6f3570e0/src/Ryujinx.HLE/HOS/Kernel/Threading/KThread.cs).

No Android ARM64, ArmProcessContextFactory seleciona NceEngine com MemoryManagerNative. Somente essa combinação Bionic habilita o novo provider. JIT, AArch32, Linux/macOS NCE continuam no getter anterior. A frame UAPI ARM64 tem GPRs em184, SP432, PC440, PSTATE448 e área reservada464; FPSIMD pode vir após outros registros. [UAPI Linux v6.1](https://github.com/torvalds/linux/blob/v6.1/arch/arm64/include/uapi/asm/sigcontext.h). Não certifica o kernel vendor/NativeAOT do Thor.

## Contrato implementado

- Novo IThreadContextProvider devolve CpuThreadContext por valor. KThread inicializa PC de entrada antes de executar, confirma force-pause, ausência dos cores/pinning e mantém as travas existentes até terminar a cópia. Falta de fonte válida retorna InvalidState; não inventa estado. A critical section é liberada em finally.
- Contexto recebe quatro campos finais (32 bytes com alinhamento ARM64): origem, PC do helper, tipo e destino original de leitura. Os offsets anteriores ficam iguais. GetX/GetV/Pc e storage de execução não mudam.
- Sinais guest publicam a ucontext antes do callback já existente; deferral 1/3 e cadeia host da R10 fix não publicam nem substituem essa origem. O parser completo roda na thread que consulta, sem nova entrada gerenciada pelo handler. A frame é emprestada enquanto a thread target está parada; a consulta protege lifetime contra Dispose e faz uma barreira somente no leitor após observar a pausa. O scheduler já tem a fence Interlocked de Unlock antes de retirar o core.
- SVC/contador publicam a área de salvamento e um ADR do helper antes do callback. SVC combina X0–X7 de storage com os demais valores da pilha. Contador usa o resultado em TempStorage para seu destino quando pausa após a leitura. PC é o sucessor da instrução nesse caminho; SP exclui o save area. FP/PSTATE vêm do estado guest preservado.
- Metadados de leitura são registrados em lote por módulo depois da publicação aprovada, fora da execução guest. Para pausa no meio de prologue/epilogue, usam as instruções reais de save/restore para reconstruir SP, GPR/V e NZCV, incluindo SUB/ADD de frames maiores que512B e instruções TLS. Uma leitura TLS guarda somente seu destino original antes de transformá-lo em ponteiro temporário. Na retomada, nada dessa captura é escrito na frame/pilha/contexto de execução.
- Metadados vivem por memory manager, com weak table, busca binária e retirada por UnmapEvent. Frames/código/pilhas não disponíveis e FPSIMD ausente/inválido produzem falha da consulta. Não há cache temporal do snapshot, alteração de sigreturn ou máscara, cópia completa em todos os faults, pausa adicional ou flush de código.

Isso cobre o estado da API ThreadContext AArch64: X0–X28, FP/LR, SP/PC, V0–V31, PSTATE com máscara HLE existente, FPCR/FPSR e TPIDRRO virtualizado conforme o contrato Neo. Não passa a reportar TLS host.

## Custos e classificação

Correção funcional necessária com evidência de consumidor concreto. Não é ganho de FPS prometido. Por SVC: cinco instruções de publicação; por contador: seis; por helper de leitura TLS: uma store; por sinal guest atendido: três instruções. Entrada Android acrescenta duas instruções para indicar a fonte inicial. Sem syscall, lock, barreira ou salvamento completo adicional nesses caminhos; não altera as reservas STLXR já aprovadas.

Consulta: os locks HLE existentes, um lock de lifetime, um lock de busca e uma barreira de leitura; cópia de aproximadamente0,8KiB de estado. Sinal lê até4560B da frame; helper lê até cerca de0,8KiB de save area. Aloca buffers somente na consulta. Não há escrita de GPR/V guest nesse caminho.

Loader: metadados proporcionais aos sites, maior ordem de grandeza cerca de1–2KiB por helper SVC/contador e menos para TLS, a confirmar por execução. Registro/sort é em lote por módulo, evitando ordenar a lista por cada instrução. Esse custo de memória/carregamento e as instruções extras do contador podem ter impacto: medir no Thor, principalmente Doom/MK11/Zeldas. Nenhum custo em nanossegundos ou ganho foi atribuído sem medição.

Instrumentação: marcador de início `[NEO-NCE-R12] pausedContext=on-demand frame=signal-or-trampoline unwind=guest-registers`, fora dos sinais. Telemetria anterior permanece. Nenhum log/contador gerenciado novo dentro do handler.

## Verificação e critérios

37 casos preparados: NceThreadContextSnapshotTests29 e NceSnapshotPublicationTests8. Emitem helpers/handlers Android reais em Unicorn, consultam o reader real e verificam todas as fronteiras nativas de SVC, contador e TLS, destinos0/18/19/29/30 e MSR XZR, fontes gerenciadas/pending, parser FPSIMD reordenado/malformado, campos completos, SP/PC, mask HLE e ausência de escrita na memória de execução. As fixtures R1–R11 continuam obrigatórias. Sinais/scheduler/kernel são simulados; não significam aprovação Android.

Codex: apenas parsing, hashes, diffs e invariantes; sem compilar/executar testes/probes/APK/commit/push/aparelho. Antigravity executa testes e build; usuário compara com R11. Reprovar por novo crash NCE, erro de consulta, estado antigo/incompleto, corrupção ao retomar, fechamento quebrado, regressão consistente de FPS/pacing ou custo excessivo de loader.

Não verificados: uso efetivo de GetThreadContext3 pelos jogos testados; máscaras/altstack/kernel/GC/TLS reais; corridas multicore; SVE/SME/PAC/BTI; alterações concorrentes de código ou mapeamentos e reutilização de VA enquanto uma thread continua dentro do módulo antigo. R12 não introduz leases/generation globais M04, nem certifica essas hipóteses. O leitor recusa fontes indisponíveis em vez de preencher com dados antigos; concorrência de reutilização continua exigindo evidência específica.

Reversão: apenas os sete arquivos existentes desta rodada a partir de work/nce-r12/before e retirada dos seis arquivos novos, depois de conferir mudanças posteriores. Não executar reset nem restaurar R10 inicial. Preservados R11, botão fechar, QoL1–4, identidade Experimental e TextureGroup SHA2569E9720C8891523D5469214C46E7C5960CDD8960F9A79D72056B959F71F439A36. Current não é alvo. Restam F05 NRO dinâmico e F08 permissões/tracking, mais validação final; R11/R12 ainda dependem de avaliação prática.
