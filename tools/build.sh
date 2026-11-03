#!/usr/bin/env bash
# Gera as duas edições do app e roda a bateria de testes (tests/testar.ps1) em cada .exe gerado.
# Termina com erro se qualquer teste falhar — só publique o que passou aqui.
#
#   tools/build.sh [normal|comprimido|reduzido]     (padrão: reduzido)
#
#   normal      ~95 MB  runtime .NET inteiro, sem compressão
#   comprimido  ~45 MB  mesmo conteúdo, comprimido dentro do .exe
#   reduzido    ~18 MB  comprimido + remove do .NET o que o app não usa (trimming parcial)
#
# Roda no WSL; os testes rodam no Windows (powershell.exe), onde está o certificado.
set -euo pipefail

modo=${1:-reduzido}
raiz=$(cd "$(dirname "$0")/.." && pwd)
comum=(-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
       -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none)

case $modo in
  normal)     extra=() ;;
  comprimido) extra=(-p:EnableCompressionInSingleFile=true) ;;
  reduzido)   extra=(-p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial
                     -p:JsonSerializerIsReflectionEnabledByDefault=true) ;;
  *) echo "modo desconhecido: $modo (use normal, comprimido ou reduzido)"; exit 2 ;;
esac

cd "$raiz/src/NfseApp"
publicar() {  # $1 = teste|producao, demais = opções extras
  local edicao=$1; shift
  # Pasta intermediária própria por modo/edição: nada de reaproveitar compilação de outra variante.
  dotnet publish "${comum[@]}" "${extra[@]}" "$@" \
    -p:IntermediateOutputPath="obj/$modo-$edicao/" -p:OutputPath="bin/$modo-$edicao/" \
    -o "$raiz/dist/$modo/$edicao" | grep -E "error|NfseApp.* -> .*dist" || true
}
publicar teste
publicar producao -p:SoProducao=true

falhou=0
for exe in "$raiz/dist/$modo/teste/NfseApp-Teste.exe" "$raiz/dist/$modo/producao/NfseApp.exe"; do
  [ -f "$exe" ] || { echo "não gerou $exe"; exit 1; }
  echo
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$raiz/tests/testar.ps1")" \
    -Exe "$(wslpath -w "$exe")" || falhou=1
done

echo
if [ $falhou -eq 0 ]; then echo "TUDO OK ($modo): pode publicar dist/$modo/"; else echo "HOUVE FALHA ($modo): não publique"; exit 1; fi
