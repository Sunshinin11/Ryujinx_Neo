# Contribuindo com o Ryujinx Neo

Obrigado pelo interesse no projeto. O Ryujinx Neo é um **fork pessoal e experimental** da linha Android/Bionic do Kenji-NX, construída sobre o trabalho original do Ryujinx. Há mudanças próprias, correções estudadas em outros projetos e código produzido com assistência de IA. Leia o [README](README.md) para entender a origem e os créditos antes de propor alterações.

## Relatar um problema

Procure uma issue semelhante antes de abrir outra. Ao relatar um bug, inclua, quando puder:

- jogo e versão, além do ponto exato onde o problema aparece;
- aparelho, versão do Android, GPU e driver Vulkan;
- commit ou versão do Ryujinx Neo;
- modo NCE ou JIT, modo Dock/Portátil e opções por jogo alteradas;
- passos para reproduzir, resultado esperado e resultado observado;
- log e capturas de tela, removendo dados pessoais antes de publicar;
- informação sobre regressão: em qual versão anterior funcionava.

Não anexe jogos, firmware, chaves ou outro conteúdo protegido.

## Propor uma mudança

Explique o problema, a hipótese e o efeito esperado. Uma boa alteração mantém um escopo claro, permite comparar antes/depois e informa quais jogos e aparelhos foram testados. Para mudanças em NCE, Vulkan ou shaders, descreva também riscos de regressão e como a opção pode ser desativada ou revertida.

Se a mudança foi inspirada ou adaptada de outro projeto, **cite a fonte, o commit ou a documentação técnica**. Preserve avisos de licença e créditos. O código herdado de Ryujinx e Kenji-NX não deve ser apresentado como criação original do Neo.

## Uso de IA

O projeto usa **vibe coding** e ferramentas de IA de forma aberta. Contribuições com assistência de IA são bem-vindas, mas a descrição deve dizer como a ferramenta foi usada e quais partes foram verificadas por uma pessoa. Não declare um teste como concluído se ele não foi executado. Trechos não testados devem ser identificados como tal.

## Validação

O projeto tem caminhos diferentes para Android, desktop e bibliotecas internas. Informe qual variante foi compilada e em qual ambiente a mudança foi testada. Para desempenho, mantenha jogo, save, driver, configuração e temperatura tão constantes quanto possível; uma única medição não prova ganho geral. Para correções gráficas, compare também a imagem, não apenas o FPS.

As [orientações de estilo C#](docs/coding-guidelines/coding-style.md) herdadas da base continuam como referência. O [changelog](CHANGELOG.md) e o [relatório técnico histórico](docs/RELATORIO_COMPLETO_RYUJINX_NEO.md) ajudam a entender decisões anteriores, inclusive tentativas que não deram resultado.

## Licença e créditos

Ao contribuir, respeite a [licença MIT](LICENSE.txt) do repositório e as licenças de componentes de terceiros em [THIRDPARTY.md](distribution/legal/THIRDPARTY.md). Se encontrar crédito ausente, abra uma issue com a fonte correspondente.

