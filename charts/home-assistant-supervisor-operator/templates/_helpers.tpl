{{/* Old release values reused during an upgrade can omit new chart defaults. */}}
{{- define "haso.trustPodNetwork" -}}
{{- $ingress := .Values.ingress | default dict -}}
{{- if hasKey $ingress "trustPodNetwork" -}}
{{- get $ingress "trustPodNetwork" -}}
{{- else -}}
true
{{- end -}}
{{- end -}}
