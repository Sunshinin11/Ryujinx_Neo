# Experimental NCE R13 — NRO dinâmico (F05)

Versão Android **0.0.19-nce-r13 / 20126**, Ryujinx Neo Experimental, pacote separado org.kenjinx.android.experimental. Implementação sobre a R12 real; a aprovação prática da R12 não foi informada. Sem promoção ao Current.

## Evidências e referências

HEAD local de Experimental e Current: `d24e22d2d2714fecdae89377f1261ac49d79e3b1`, branch `neo-v2-nce2`. Branch remota conferida nesta rodada: `06b96f2a7103bffe44a63bea01e0a774e8ce2d7c`. O [changelog](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/CHANGELOG.md), incluindo a fundação NCE, exclusivas e serialização RequestInterrupt/Dispose, foi relido e preservado. Nenhum pull/merge foi feito. Quatro arquivos de teste já diferiam do manifesto da R12; a R13 preserva integralmente essas alterações externas e todo o runtime da R12.

No Neo, [IRoInterface.LoadNro/SetNroMemoryPermissions](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.HLE/HOS/Services/Ro/IRoInterface.cs#L426) não chama o patcher NCE. O loader inicial `ProcessLoaderHelper.LoadIntoMemory` já chama `NceCpuCodePatch.Write`; esse caminho permanece intacto. O histórico local do serviço foi consultado, além do código real com R1–R12.

No snapshot de Eden `d16735f5b618942136d6ab53466e3be0a382c30a`, [RoInterface.MapManualLoadModuleMemory](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/core/hle/service/ro/ro.cpp#L428) chama PatchText/RelocateAndCopy sob HAS_NCE/IsNceEnabled e mapeia memória de patch após o NRO. Essa é evidência da necessidade de tratar módulos dinâmicos; seu alocador e lifetime não foram copiados. Ryubing local está em `475615f0431b4995b03c930c5089d20f6f3570e0`: [IRoInterface.LoadNro](https://git.ryujinx.app/projects/Ryubing/src/commit/475615f0431b4995b03c930c5089d20f6f3570e0/src/Ryujinx.HLE/HOS/Services/Ro/IRoInterface.cs) não oferece esse caminho NCE Android. Os snapshots históricos foram relidos; não se verificou um novo tip de Eden/Ryubing. O diagnóstico F05 foi confirmado no código desta rodada, sem reaplicar patches descartados.

## Caminho e mudança

`IRoInterface.LoadNro → ParseNro → NroNceMapping.Load → NcePatcher.CreatePatch → publicação → registro`. A seleção exige Android/Bionic e o `MemoryManagerNative` realmente associado ao processo. JIT, ARM32 e outras plataformas mantêm o carregamento anterior. O NRO principal continua no loader inicial existente.

`ParseNro` permanece intacto: SHA-256/NRR e duplicidade são verificados antes de `ApplyNroPatches`. A imagem depois dos mods alimenta o mesmo patcher dos NSOs: SVC, TLS, contador, XZR e exclusivas com o filtro aprovado. Não há um segundo conjunto de regras de instruções.

Uma ilha de páginas do pool do processo é reservada antes da imagem, com guards externos de 16 KiB e guard entre ilha e imagem. A alocação e os aliases são confirmados pelo kernel; colisão ou guard ocupado desfazem a tentativa. O preflight valida os dois branches imm26 de cada site antes de escrever nos aliases. Uma imagem que não cabe no alcance retorna erro controlado; esta rodada não adiciona branches longos.

Texto/dados/BSS são escritos, o patch e a metadata da R12 são publicados, permissões RX/R/RW são aplicadas e os caches do texto/ilha são mantidos somente depois de os aliases executáveis existirem. Apenas então o serviço registra o NRO e devolve seu endereço.

`UnloadNro`, falha parcial e descarte do serviço usam o mesmo proprietário. Ele acompanha BSS, porção de dados mutable, prefixo de texto/RO, lista física da ilha e reserva de memória separadamente. Falhas de cleanup mantêm o proprietário para nova tentativa; o serviço tenta novamente antes de outro load e no descarte. A proteção RW da origem emprestada é restaurada porque `UnmapProcessCodeMemory` só atualiza essa permissão na metadata do kernel. As palavras ainda iguais ao patch NCE voltam ao conteúdo anterior ao NCE; outras escritas do jogo e os dados são preservados. Os eventos de unmap existentes invalidam a metadata da R12. A reserva da ilha é liberada uma única vez após cleanup bem-sucedido.

## Custos e limites

Custos somente em load/unload: uma varredura adicional do texto, memória dos trampolins/descritores, locks de alocação, queries, tracking e contabilidade, map/unmap e reprotect dos novos ranges. Não há locks, sinais, syscalls ou pausas novos por instrução/quadro. A reserva de recurso usa timeout zero. O caminho existente de Write aplica RX diretamente à ilha e a atualização posterior no kernel repete esse reprotect; isso custa uma chamada adicional por ilha, evitando mudar o Write aprovado. A manutenção de cache percorre texto e ilha em linhas do host; em 64 B, aproximadamente duas passagens por `(texto + ilha)/64`, além de barreiras por range físico/virtual. Esses custos não foram medidos no Thor.

O contrato exige unload legal, com o jogo não executando o módulo descarregado. Não se certificou unload ilegal concorrente, reutilização arbitrária de código pelo jogo, exaustão do slab no teardown final, kernel real nem todas as situações de tracking. Falhas de cleanup são registradas e não tratadas como sucesso; falha persistente no descarte final depende do encerramento do processo. A composição global de permissões/tracking permanece para R14 (F08).

## Validação e reversão

Sem testes executados, compilação, APK, commit ou aparelho por Codex. Foram preparados 19 casos para restauração por alias, reload na mesma VA, preservação de escritas, patch somente in-place, preflight e guards/overflow; eles não validam a transação inteira do kernel Android. O prompt do Antigravity pede somente o APK. Validar no aparelho entrada em jogos, cargas/trocas de áreas e módulos, fechar/reabrir e estabilidade comparada à R12. `[NEO-NCE-R13] nro=...` identifica um load dinâmico realmente usado; sem essa linha, uma sessão estável não comprova o novo caminho. Não se promete ganho de FPS ou correção de textura.

Backup dos cinco arquivos anteriores em `work/nce-r13/before`; três arquivos novos registrados no manifesto. Reverter somente esses deltas após verificar mudanças posteriores, sem reset e sem tocar as rodadas anteriores, QoL, GPU/texturas ou Current. Resta uma frente de implementação: R14, permissões/tracking; aprovações práticas e validação final continuam separadas.
