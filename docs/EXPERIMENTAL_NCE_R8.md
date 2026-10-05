# NCE R8 — adquirir estado antes da transição SVC/contador

Data: 02/10/2026. Versão **0.0.13-nce-r8 / 20120**, nome Ryujinx Neo Experimental, pacote `org.kenjinx.android.experimental`. Base: R1 + QoL1/2/3 + R2/R3/R4/R5/R6/R7 + QoL4. O usuário determinou manter R7 e avançar. Não houve reversão para R6. A preparação cancelada em `work/nce-r7-revert` não é uma rodada aplicada e não é fonte de build.

## Evidência, preservação e escopo

Current e Experimental seguem no HEAD `d24e22d2d2714fecdae89377f1261ac49d79e3b1`, branch `neo-v2-nce2`; rodadas aprovadas estão em alterações locais. Antes de editar, foram conferidos os 55 arquivos dos manifestos sobrepostos e capturados hashes de todos os 4.432 arquivos rastreados em cada pasta. Não resetar HEAD nem reaplicar experimentos descartados.

O [changelog remoto](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/CHANGELOG.md) foi relido por API, na ponta novamente confirmada `06b96f2a7103bffe44a63bea01e0a774e8ce2d7c`, blob `b3b4c60d591eac02720aa5747c919063de60696d`. Não importar o commit inteiro, reader NCZ/libzstd ou PR6. Preservar toda a QoL, a serialização RequestInterrupt/Dispose, filtro/ordenação das exclusivas, cache/publicação, relógios e tracking existentes. O arquivo de envio seletivo de texturas `TextureGroup.cs` permanece exato, SHA-256 `9E9720C8891523D5469214C46E7C5960CDD8960F9A79D72056B959F71F439A36`, incluindo a melhoria [339470ed](https://github.com/Sunshinin11/Ryujinx_Neo/commit/339470edc8b891c3994a36931027750380fa835e).

Foram relidos `WriteSvcPatch`, `WriteMrsCntpctEl0Patch`, `WriteManagedCall`, helpers R6, entrada R4, suspensão R5 e wrapper R7. O histórico/blame atribui a troca de HostSp antes de `writeCall` a `e8e4c5aa61` (10/05/2025); preservação NZCV/FP entrou em `f24d6f7068` (19/09/2026). [Fonte histórico do patcher](https://github.com/Sunshinin11/Ryujinx_Neo/blob/d24e22d2d2714fecdae89377f1261ac49d79e3b1/src/Ryujinx.Cpu/Nce/NcePatcher.cs). Os links históricos não substituem os hashes das fontes locais aprovadas.

Eden e Ryubing continuam referências do estudo original, sem nova importação: Eden `d16735f5b618942136d6ab53466e3be0a382c30a`; Ryubing commit `475615f0431b4995b03c930c5089d20f6f3570e0`, árvore `efbcc01d8b4553c1ae7f0b485c7e3fa249c3b21f`. Não foram reconsultadas novas pontas desses projetos nem presumida equivalência de TLS, sinais ou JIT com Android NCE.

## Caminho atingido e condição de falha

Android NCE: `CreatePatch` identifica SVC ou leitura de CNTPCT_EL0 e emite o respectivo patch. Ambos chamam `WriteManagedCall`. A entrada salva os GPR/V necessários na pilha guest, lê NZCV e encontra contexto pelo índice/tabela existente. Antes da R8, publicava NZCV/FP, carregava FP host, salvava GuestSp e trocava para HostSp; só dentro da chamada específica adquiria `InManaged`.

`RequestInterruptCore` marca pending com OR e só envia SIGUSR2 quando o valor anterior era zero; no Bionic, `NceThreadPalAndroid.SuspendThread` usa pthread_kill. Se o pedido acontece entre a troca para HostSp e o acquire antigo, encontra estado zero e publica 2. O handler R5 considera 2 execução nativa e usa o mesmo HostSp. A ordem permite reutilizar a pilha host já ocupada pela transição e capturar FP host como guest. A frequência e ocorrência real no Thor não foram demonstradas. Não atribuir a isso o defeito intermitente do Doom.

Depois da R8: prologue guest → guardar NZCV em scratch → lookup → **acquire na pilha guest** → publicar NZCV/FP → selecionar FP/HostSp → callback específico → release e retorno existentes. Se houver 2, o acquire aguarda antes de ocupar HostSp. Se adquirir 1, o pedido normal marca 3 e o release existente o atende; uma entrega gerenciada encontra a defesa R5. Não usar a hipótese simplificada, já refutada, de um pedido comum publicando 2 que seria ignorado por acquire.

O acquire usa scratchRegs[0/1/2], deixando scratchRegs[3] com NZCV guest. ADD/SUB sem flags, LDAXR/STLXR, MOV e CBNZ do helper não modificam NZCV. O contexto retorna à base antes do snapshot. Os dois acquires específicos foram removidos: cada patch continua adquirindo uma única vez. SVC mantém X0–X7 em storage e sua política de resultado/false; contador mantém resultado em TempStorage, destino e fonte de ticks. A proteção XZR R1 continua intacta.

Não altera o release nem o retorno de qualquer sinal. **F02 segue parcial:** a saída ainda publica nativo antes de restaurar FP/SP; F03 (saída por sinal), F04 (origem gerenciada da falha), F11 (snapshot completo), F05 (NRO) e F08 (permissões/tracking) continuam separados. Não tentar resolver essas partes por um Ret, bloqueio de sinais por syscall ou cópia geral de registradores.

## Classificação e custos

| Item | Classificação | Custo e limite |
|---|---|---|
| Claim antes de snapshot/FP/HostSp em SVC e contador | Correção funcional com evidência de ordem/invariante | Move o helper existente: zero novas instruções no caminho de produção, zero locks/syscalls/barreiras/frames/registradores extras. |
| Espera por estado 2 e retry | Já cobertos pelo helper R6 | Mudam de posição; continuam aguardando o serviço existente. Contenção e latência não medidas. |
| Ganho de FPS/fluidez | Depende de medição | Não há perfil ou resultado Android novo. Não apresentar como ganho confirmado. |
| Telemetria | Instrumentação R1 preservada | Sem contadores/log/clock novos no handler. Não mede a espera do acquire nem identifica texturas. |
| Alterações de renderer, compressão de texturas ou fila GPU | Fora do escopo | Nenhuma alteração, inclusive envio seletivo 339470ed preservado. |

O trecho marcado gerenciado começa antes e pode adiar pedidos até o release. Zero instruções adicionais não significa latência idêntica. Medir resposta a pausa/fechamento e estabilidade dos quadros.

## Testes preparados e limitações

Fixture nova `NceManagedTransitionTests`: **28 casos preparados**. Unicorn executa entrada R4 real, que cria a frame host, e os patches SVC/contador reais com contexto/callbacks sintéticos selecionados durante geração. Ponteiro zero conserva resolver e callbacks de produção; não há branch de escolha de teste emitido no ARM64 de produção. Os overloads de um parâmetro continuam presentes e a reflection usa assinatura exata.

Cobertura: aquisição antes de qualquer publicação de snapshot ou troca FP/SP; contador X0/X19/X20/X21/X22/X23/X30; NZCV, FP, todos GPR e Q preservados; resultado SVC atualizado em storage; resultado de contador sobrevive ao callback de pending; espera por 2; reserva perdida por CLREX; pedido entre load/store; oito entregas gerenciadas 1/3, antes/depois de HostSp, executando o handler R5 real; três políticas de false retornam pela frame host criada pela entrada real. A frame sintética de sinal restaura registradores e NZCV explicitamente.

Snapshot em storage é tornado deliberadamente antigo após a entrada real, para que publicação prematura seja observável. Callbacks modelam clobber ABI, preservando X18 e baixos D8–D15, e podem alterar a metade alta dos V callee-saved. Apenas o branch final B(0) do patch é ligado ao ponto de continuação no harness.

O harness não instala sinais, não executa callbacks .NET pela ABI nativa, não prova TLS/index real, kernel, scheduler, concorrência multicore, altstack/máscaras reais ou Android NativeAOT. Pending=2 e invalidação de reserva são modelados; não são medição de entrega de sinal. Windows não emite o snapshot Linux do handler nativo, preservado nas fontes. Stop dos testes é pelo retorno de SVC/contador, **não** prova F03 no retorno por sinal. Necessário executar fixtures e validar APK no Android.

Codex faz parsing sintático sem compilar, hashes, escopos e checks de patch. **Não compilou, executou testes, gerou APK, fez commit/push ou acessou o aparelho.** Parsing não comprova semântica. Antigravity executa testes/probes/build/inspeção pelo prompt. Testes existentes R1–R7 e QoL4 são obrigatórios e permanecem exatos.

## Validação, Doom e reversão

O usuário mantém R7 e informou um teste de aproximadamente cinco minutos sem corrupção. Registrar intermitência; não declarar regressão descartada, defeito corrigido ou R7 como causa. Comparar R8 com R7 sob mesma cena/save/cache/Turnip/limite de FPS/configuração/temperatura inicial. Priorizar Doom 2016, MK11, Zeldas e Metroid Prime 4; usar sessões repetidas suficientemente longas, anotando tempo até corrupção/engasgo, FPS e fluidez, áudio/input, pausa e fechar/reabrir. Não mexer no envio seletivo nem limpar cache para mascarar o problema.

Reprovar por crash/congelamento, término travado, pedido perdido ou piora repetível exclusiva à R8. Uma sessão sem defeito não prova ausência de regressão. Preserve botão sumindo no primeiro clique e todas as QoL, inclusive conversões e instalação Experimental separada.

Quatro arquivos: TRANSITION (patcher + fixture nova), METADATA (versão + documento). Base arquivada em `work/nce-r8/before`; manifestos e patches completos/separados em outputs. Uma reversão futura deve retirar somente R8 e manter R7/QoL4, após conferir hashes e reverse-check. Não usar reset/checkout para d24 nem usar a preparação cancelada de retirada R7. Qualquer APK posterior precisa versionCode maior e identidade correta. Nenhuma reversão é executada nesta rodada.
