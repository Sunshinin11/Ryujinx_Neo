# Ryujinx Neo Experimental — QoL1, QoL2 e ajuste visual QoL3

Data: 02/10/2026. Base Git `d24e22d2d2714fecdae89377f1261ac49d79e3b1` **mais a working tree da rodada NCE R1 aprovada pelo usuário**. Não restaurar somente o commit-base: ele não contém os ajustes NCE aprovados, que permanecem sem commit.

Esta rodada trata de mods por pasta, descompactação e idioma por jogo. Não muda os arquivos CPU/NCE, sinais, PROT1, telemetria NCE, filtros de exclusivas ou flags GPU. `NczSupport` recebe somente um método adicional de exportação offline; o caminho de leitura usado durante a emulação permanece.

Identidade Android preservada: **Ryujinx Neo Experimental**, pacote mainline `org.kenjinx.android.experimental`. Versão atual `0.0.5-qol3`, código `20112`; usar `MainlineArmv8Release` e a mesma assinatura do APK Experimental aprovado, para atualizar conservando seus dados.

Após o usuário validar QoL1, QoL2 ajusta a caixa de descompactação à identidade do menu do jogo e, a pedido do usuário, exclui o NSZ/NCZ original somente após verificar e publicar o NSP/NCA. Usa o mesmo `ActionCard`, agora compartilhado entre os dois diálogos; ações antigas mantêm o estado habilitado por padrão. Não altera o backend C# de conversão, mods, idioma ou NCE.

O usuário rejeitou o resultado visual QoL2. QoL3 remove a caixa separada na biblioteca: a descompactação passa a ser um painel dentro do próprio `GameActionsDialog`, preservando capa, título, Title ID, dimensões e cabeçalho do compactador. As ações usam a mesma `LazyVerticalGrid` de uma/duas colunas e os mesmos cartões, incluindo Voltar. O conteúdo de progresso original do compactador foi extraído para `ConversionProgressContent` e é utilizado pelas duas conversões, mantendo a apresentação e alterando somente os textos por operação. No acesso pelas configurações, o diálogo para arquivos usa as mesmas dimensões e o mesmo painel/grade/progresso. A descrição em Configurações também informa que o original será excluído após verificação. `NszConverter.kt` e o backend C# permanecem idênticos a QoL2.

Preparação QoL3: parsing de três arquivos Kotlin, conferência da preservação do frame/cabeçalho/ActionCard e das opções do compactador por comparação dos trechos existentes. Não houve build nem inspeção visual do novo APK no aparelho; comparar compactar/descompactar em temas claro/escuro e retrato/paisagem na próxima validação.

## Referência Eden realmente consultada

Mirror `eden-emulator/mirror`, commit `d16735f5b618942136d6ab53466e3be0a382c30a`, mesmo snapshot fixado no estudo anterior. Não há checkout local do Eden em `C:\Desenvolvimento`; os arquivos foram lidos pelo conector GitHub.

- [`AddonsFragment.installAddon`](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/android/app/src/main/java/org/yuzu/yuzu_emu/fragments/AddonsFragment.kt): usa `OpenDocumentTree`, valida diretórios reconhecidos na pasta escolhida e copia para uma pasta do jogo com o nome da pasta selecionada. Atualiza a lista após a cópia. É instalação por cópia, não execução permanente do mod no endereço externo.
- [`AddonUtil.validAddonDirectories`](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/android/app/src/main/java/org/yuzu/yuzu_emu/utils/AddonUtil.kt): reconhece formatos adicionais próprios do Eden.
- [`AddonViewModel`](https://github.com/eden-emulator/mirror/blob/d16735f5b618942136d6ab53466e3be0a382c30a/src/android/app/src/main/java/org/yuzu/yuzu_emu/model/AddonViewModel.kt): mantém listagem, refresh e estado dos addons.

A implementação Neo segue o fluxo de seleção/cópia pelo Android, usando seus caminhos e formatos. O loader local `src/Ryujinx.HLE/HOS/ModLoader.cs` (`AddModsFromDirectory`, `QueryApplicationDir`) reconhece romfs, exefs, cheats e containers romfs.bin/exefs.nsp. Não se adiciona suporte a romfslite/romfs_ext somente porque o Eden aceita esses nomes.

## Mods por pasta

Na biblioteca: menu do jogo → **Mods → Importar pasta**. Selecione a pasta que contém `romfs`, `exefs` ou `cheats`, por exemplo:

```text
Tradução PT-BR/
  romfs/
    ...
```

Uma pasta com vários mods diretamente dentro também é aceita. Não se varre arbitrariamente todo o armazenamento; selecione o mod ou a coleção imediata. Os containers já aceitos pelo loader (`romfs.bin`, `exefs.nsp`) são reconhecidos. Arquivo ZIP continua disponível pelo botão separado.

Destino existente do Neo: `files/sdcard/atmosphere/contents/<TITLEID>/<nome do mod>/`. A origem é somente lida. Não é preciso manter a pasta original depois de uma importação concluída. Mod com nome já instalado é recusado com mensagem; não substitui nem apaga o anterior. Para atualizar, o usuário pode renomear a pasta ou remover o mod antigo conscientemente pelo gerenciador existente.

A cópia usa uma pasta temporária fora de `contents`, no mesmo volume do destino. Publica os diretórios após terminar a cópia. Em falha tenta limpar os diretórios desta operação; falha numa coleção tenta reverter os novos itens já publicados. Não toca mods anteriores. Não garante uma transação entre vários diretórios se o processo for morto no instante da publicação. Uma morte abrupta também pode deixar uma pasta temporária `.mod-import-*` sem aparecer na lista de mods.

Nomes não podem escapar da pasta de destino; há validação do Title ID, nomes repetidos e limite de profundidade de 64. As listagens SAF são consultadas explicitamente, para que erro de provedor não seja aceito como uma pasta vazia. Cópia compara tamanho quando disponível e sincroniza cada arquivo. Não valida se o patch é compatível com a versão/build do jogo; isso depende do mod, como no fluxo anterior.

## Descompactação

- No menu de um jogo NSZ: **Descompactar → NSP**. O destino inicial é a pasta do jogo; pode escolher outra.
- Em **Configurações → Jogos e Dados → Descompactar NSZ ou NCZ**: escolha arquivo e pasta de destino. Permite **NSZ → NSP** e **NCZ avulso → NCA**.
- Um NCA avulso é conteúdo; descompactá-lo não transforma automaticamente esse arquivo em um pacote NSP completo. XCZ → XCI não faz parte desta rodada.

A partir de QoL2, no sucesso o arquivo compactado original é excluído, como no compactador NSP → NSZ anterior. A exclusão só é tentada após verificação nativa, fsync, fechamento dos dois descritores, publicação e confirmação do nome/tamanho final. Em erro ou cancelamento antes dessa conclusão, a origem é mantida. Se o provedor negar ou falhar a exclusão, a saída verificada é mantida e a mensagem informa que os dois arquivos permaneceram. Se o cancelamento chegar logo após a publicação, a última checagem conserva a origem e informa que a saída verificada também existe. Não há garantia de interromper uma exclusão já em execução no provedor. Um destino com o nome final já existente continua recusado.

O backend calcula o tamanho de saída a partir dos metadados, valida espaço disponível no destino e grava em `.part` com nome único. O arquivo é relido e seu SHA-256 comparado antes da renomeação. Para NCA cujo nome seja um content ID de 32 hexadecimais, também compara esse ID com os primeiros 16 bytes do SHA-256 reconstruído. Para nomes arbitrários, a checagem confirma consistência da cópia, sem um hash externo confiável da origem original. O NSP reconstruído conserva bytes de NCA/tickets/metadados, mas seu cabeçalho PFS0 pode ter alinhamento diferente do NSP anterior à compactação: não prometer igualdade do hash do pacote inteiro com um NSP ausente.

NCZ sólido e NCZBLOCK são exportados por streaming. O novo método `NczFile.ExportNca` usa o parser e a recriptografia existentes, sem gerar o cache de reprodução de NCZ sólido. Trabalha com buffer de 1 MiB, mais buffers de AES/zstd e metadados; não promete um limite total de RAM de 1 MiB. Seções sobrepostas, tamanho inconsistente ou criptografia não suportada são recusados na exportação offline.

Progresso/cancelamento passam pelas novas funções nativas `deviceDecompress*`. Conversões ficam em thread de trabalho e compartilham o bloqueio de operação da interface com o compactador existente. Cancelamento é observado entre leituras/blocos; um provedor travado numa operação de I/O pode atrasar a resposta. FDs de origem são abertos somente para leitura. O destino precisa suportar busca, releitura, consulta de espaço e rename; provedores incompatíveis falham sem modificar a origem.

Antes de publicar, confere novamente ausência do nome final e tamanho. A garantia de rename depende do provedor Android; não foi testada uma disputa com outro programa criando o mesmo arquivo simultaneamente. Em erro/cancelamento a UI tenta remover somente seu temporário. Uma morte abrupta antes da publicação pode deixar `.part`, conservando a origem. Após confirmação da publicação, a origem pode ser removida; não foi testada a durabilidade do rename diante de queda de energia em todos os provedores.

Logs nativos da conversão usam `NEO-QOL1-DECOMPRESS`, além dos logs existentes do parser NCZ. Não há envio externo de dados, novas métricas no loop de emulação ou alterações dos contadores NCE aprovados.

## Idioma por jogo

No menu do jogo → **Configurar → Sistema e CPU → Idioma do jogo**. Ative configurações personalizadas e escolha um idioma ou **Usar idioma global**, depois salve. Os 18 valores do enum já existente são apresentados com nomes legíveis; o enum e seus ordinais não foram alterados.

O override é salvo pelo nome do enum em `neo_per_game_settings`, sob o Title ID. Perfil desligado, valor vazio ou desconhecido usa o global. `Restaurar global` limpa também esse override. Não modifica a preferência global nem a região. O idioma é resolvido ao inicializar o console; reinicie o jogo para aplicar. A opção informa o idioma ao jogo, mas não instala uma tradução nem garante que o jogo ofereça todos os idiomas.

`MainViewModel.loadSystemLanguage` resolve pelo `gameModel` que `loadGame` já definiu. Biblioteca e atalhos que entram nesse fluxo usam o mesmo caminho. O Mii Editor limpa `gameModel` e continua usando o global.

## Validação e testes pendentes

Preparação sem compilar o projeto/APK: revisão dos diffs; parse de sintaxe Roslyn de quatro arquivos C#; parse Kotlin PSI de 13 arquivos Kotlin; conferência de IDs/versão e assinatura das APIs na biblioteca LibHac instalada (`AsStorage(..., keepOpen: true)`). Parser não faz binding semântico, AOT, lint ou teste Android.

Preparados, mas não executados aqui:

```powershell
dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj -c Release --filter FullyQualifiedName~NczDecompressorTests
```

```powershell
# Em src/KenjinxAndroid
.\gradlew.bat :app:testMainlineArmv8DebugUnitTest --tests org.kenjinx.android.QualityOfLifeRulesTest
```

Testes C#: fixtures sintéticas de NCZ sólido/bloco, bloco raw/zstd, AES-CTR, conversão avulsa e NSZ, preservação de entradas/copias, nome duplicado, truncamento, content ID errado, cancelamento e corrupção do destino. Testes Kotlin: nomes/pastas e resolução/labels de idioma. Nenhum utiliza conteúdo real de jogos ou chaves do usuário.

Validação posterior no Android deve cobrir:

1. Mod de tradução e patch reais compatíveis com o jogo: pasta simples, coleção, ZIP anterior, nomes com acentos, ativar/desativar, reiniciar, confirmar efeito. Testar pasta inválida, destino existente, leitura negada/falha de cópia e conferir que o mod antigo permanece.
2. NSZ e NCZ sólido/bloco: origem/destino no armazenamento interno e SD, tamanho e hashes, executar o NSP exportado, content ID esperado do NCA. Usar cópias de teste: no sucesso deve restar apenas a saída; exclusão negada deve informar que ambos ficaram. Cancelar no início/meio/verificação; testar falta de espaço e nome existente; em falha/cancelamento original e saída preexistente devem permanecer. Testar compactador NSP → NSZ aprovado para garantir que a ação antiga continua.
3. Idioma: título A com override PT-BR e B com global inglês; salvar, fechar/reabrir aplicativo, iniciar por atalho, desligar perfil, mudar global com override herdado e restaurar global. Conferir idioma no jogo e preferência global inalterada; Mii Editor deve continuar global.
4. Retrato/paisagem, lista de mods vazia/longa, menu de idioma com todas as opções, rotação/background durante operações. Estado durante rotação e recuperação após morte do processo ainda não foram validados.

Não houve APK gerado, instalação, benchmark ou contato com o aparelho nesta preparação. Testes compilados, binding Kotlin/C#, símbolos JNA/NativeAOT, providers reais, fidelidade dos arquivos de jogos e comportamento dos três fluxos ainda precisam de validação. A aprovação de ausência de regressão NCE veio do relato do usuário para a rodada anterior, não destes checks.

## Preservação e reversão

`C:\Desenvolvimento\Kenji-NX-Current` permanece fora desta rodada. As fontes NCE aprovadas e os originais dos arquivos QoL ficam preservados na pasta de trabalho do Codex; a referência Git de recuperação aponta ao commit-base, e precisa ser complementada por esse snapshot porque o NCE aprovado estava sem commit.

O manifesto QoL registra arquivos/hash originais e aplicados. O patch QoL entregue é relativo ao estado **com NCE aprovado**, não ao commit-base isolado. Para reverter, primeiro conferir que não houve edições posteriores; usar a verificação `git apply --reverse --check` do patch QoL e só então aplicar a reversão autorizada. Não executar reset global nem reverter CPU/NCE. Se houver trabalho posterior, fazer reversão localizada a partir dos backups, preservando esse trabalho.

Antes de distribuir o APK: testes, build nativa nova, pacote/label/versão corretos e certificado igual ao Experimental aprovado. Não promover para Current nem instalar no aparelho automaticamente.
