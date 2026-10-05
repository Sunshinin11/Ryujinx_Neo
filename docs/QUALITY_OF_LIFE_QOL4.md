# QoL4 — otimizar NSZ para blocos no card Comprimir

Versão Experimental **0.0.11-qol4 / 20118**, pacote `org.kenjinx.android.experimental`, nome Ryujinx Neo Experimental. Base local d24 com QoL1/2/3 e NCE R1–R6. A R6 foi implementada e ainda aguarda validação; esta rodada não altera NCE. Current permanece intacta.

## Interface

No menu do jogo, o card **Comprimir** aparece tanto para NSP quanto para NSZ. NSP mantém Rápido (nível 8, selo Recomendado existente) e Máximo (nível 18). NSZ mostra **Otimizar NSZ para blocos** após inspeção dos metadados em IO; se todas as entradas NCZ já usam blocos, mostra **Já otimizado / O NSZ já usa leitura por blocos**, desabilitado. Pacote inválido/FD inacessível desabilita a opção e orienta reabrir o painel. A inspeção identifica formato; não promete integridade de todo payload já otimizado.

Mantém o ActionCard, paleta, bordas, Surface, cabeçalho, grid compacto/paisagem, progresso e botão Cancelar aprovados. A opção nova não recebe selo Recomendado até validação Android. Descompactar NSZ→NSP, NCZ→NCA, mods por pasta/ZIP e idiomas por jogo permanecem disponíveis.

O painel explica que o jogo continua comprimido e **pode ficar maior**. A saída é `<nome>.blocks.nsz`, na mesma pasta. Origem é excluída somente após a conversão/verificação/publicação bem-sucedidas; se o provedor negar exclusão, a saída é mantida e a mensagem informa que restaram os dois arquivos. Não substitui um destino já existente nem sobrescreve a origem durante geração.

## Implementação isolada

Nova classe `NszBlockOptimizer`, por Streams; Kotlin SAF/FD → exports `deviceOptimizeNsz*` → Optimize. Adapta o desenho de [NszBlockConverter no commit 06b96f2a](https://github.com/Sunshinin11/Ryujinx_Neo/blob/06b96f2a7103bffe44a63bea01e0a774e8ce2d7c/src/Ryujinx.HLE/FileSystem/NszBlockConverter.cs), usando **ZstdSharp.Port já existente**, sem DllImport libzstd.so.1, nova dependência ou alteração do reader. Changelog rechecado nesse commit: blob ainda b3b4c60d591eac02720aa5747c919063de60696d. Não importa a substituição do cache .ncb por .bin, alterações de Dispose ou conversão automática desktop.

Reorganiza PFS0 e converte apenas NCZ sólidos em NCZBLOCK v2, blocos de 1 MiB, nível 3; blocos sem economia ficam crus. Copia prefixo NCA/NCZSECTN, chaves/contadores e demais metadados sem reinterpretar/recriptografar o payload. Entradas NCZ já em blocos, tickets e demais arquivos são copiadas byte a byte. Não exige chaves externas/console inicializado para essa transcodificação. Não converte XCZ nem NCZ avulso; as outras operações existentes ficam separadas.

Antes de escrever, valida magic PFS0/NCZ, UTF-8/nomes duplicados, offsets/tamanhos/intervalos, seções e tabela de blocos. Limites explícitos: até 16.384 entradas PFS0, string table até 16 MiB, 256 seções NCZ e 4.194.304 blocos, com aritmética checked. Não relaxa esses limites silenciosamente para arquivos fora do formato suportado. Estima espaço conservador supondo todos os blocos crus; pode exigir mais espaço livre que a saída comprimida efetiva. Não aloca jogo inteiro em RAM: buffers de ~1 MiB, buffers Zstd e metadados; não promete RAM total limitada a 1 MiB.

Durante escrita calcula SHA-256 de prefixo e corpo descomprimido para convertidos, e bytes completos das entradas copiadas. Reabre a saída via Stream, valida PFS0/nomes/tamanhos/blocagem, releitura/descompressão/hashes e tamanho total. Detecta corpo truncado/excedente. Essas comparações confirmam equivalência com a origem lida, não autenticidade de uma origem já inválida contra content ID externo. Nossa descompactação e seus checks de content ID permanecem intactos.

Backend tem exclusão de operação e cancelamento entre chunks/blocos/leituras. UI compartilha running com compressão/descompactação, roteia Cancelar ao backend correto, atualiza progresso em thread principal, bloqueia dismiss enquanto converte e recarrega a biblioteca no sucesso pelo callback existente. Inspeção usa coroutine IO e não cria cache de reprodução. O progresso não percorre o loop NCE/jogo.

Kotlin cria `.part` único UUID, abre origem somente leitura e destino rw, consulta espaço/fstatvfs, pede conversão, fsync e fecha ambos os FDs. Confere tamanho, ausência de destino, rename, nome/tamanho publicados e cancelamento antes da tentativa de excluir origem. Cleanup remove só temporário desta operação. Provedores sem seek/rename/consulta de espaço falham preservando origem. Cancelamento após publicação mantém ambos e informa isso. Uma morte abrupta pode deixar temporário; a durabilidade de rename/energia depende de SAF/provedor e não foi testada. A operação não garante interromper um I/O bloqueado no provedor nem a exclusão já iniciada.

## Verificação e validação pendentes

Codex fez parsing sintático de três C# e três Kotlin, sem erros. Preparou **24 casos NUnit**: sólido/misto, AES-CTR, prefixos e tickets, reader real sem alterações, blocos crus sem economia, já otimizado, metadados inválidos, duplicados/sobreposição, overflow, truncamento/excesso, cancelamento em duas fases e saída corrompida. **Não executou testes, compilou, gerou APK, fez commit/push ou acessou aparelho.** Parsing não comprova semântica, JNI/JNA, NativeAOT ou layout visual Android.

Antigravity deve executar NszBlockOptimizerTests, NczDecompressorTests, QoL e regressões NCE anteriores; publicar LibKenjinx nova e conferir os cinco exports/identidade/hash do APK. Validar no Android NSP antigo, NSZ sólido/misto, NSZ já em blocos, erro/cancelamento, destino existente, falta de espaço/exclusão negada e layout em retrato/paisagem/fonte maior. Comparar NCA reconstruído antes/depois e leitura normal; depois tempo/tamanho/carregamento, sem prometer FPS. R6 continua exigindo sua validação própria.

## Preservação e reversão

Oito arquivos nesta rodada: versão, Kotlin interface/converter/dialog, JNI, nova classe, nova fixture e este documento. `NczSupport.cs`, `NczCompressor.cs`, `NczDecompressor.cs`, testes antigos, telas de descompactação, identidade/pacote e NCE permanecem exatos. Snapshot em work/qol4/before; manifesto, hashes e patches completos/componentes em outputs desta conversa. Reversão futura somente desses oito arquivos, após conferência de hashes/reverse-check; não resetar HEAD nem perder rodadas anteriores sem commits. Não executar rollback durante esta tarefa.
