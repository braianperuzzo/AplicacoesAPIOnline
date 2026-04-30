# Ações automáticas: Webhook — Enviar oportunidade para URL
No CRM de vendas PipeRun, você pode automatizar processos que serão realizados sempre que um gatilho for acionado, dando agilidade e economizando seu tempo gasto com execução de processos dentro da plataforma.

Através da ação automática de webhook - envio de oportunidade para URL, é possível enviar informações sobre oportunidades, empresas, pessoas e propostas para outros sistemas, utilizando o webhook do PipeRun. Os webhooks possibilitam o envio de dados de todos os eventos que ocorrem dentro do CRM. 

DADOS ENVIADOS VIA AÇÃO AUTOMÁTICA

Com este recurso, é possível enviar os seguintes dados:

Dados da oportunidade: título da oportunidade, valor de produto e serviço (P&S), valor de recorrências (MRR), data de previsão de fechamento, data de criação e de fechamento, observação, status e situação, origem, temperatura, tags, motivos de perda, funil e etapa de funil, dados do usuário, dono da oportunidade, valores dos campos customizados vinculados, formulários customizados, etc.;
Dados da empresa vinculada à oportunidade: nome da empresa, CNPJ, CNAE, número de telefone, endereço, situação da empresa, tamanho, segmento, tags, links do website e das páginas do Facebook e LinkedIn, valores dos campos customizados vinculados, etc.;
Dados da pessoa vinculada à oportunidade: nome da pessoa, CPF, número de telefone, endereço, dados da empresa de trabalho, cargo, tags, data de nascimento, gênero, observações, links do website e das páginas do Facebook e Linkedin, valores dos campos customizados vinculados, etc.;
Dados da proposta da oportunidade: valor da proposta em produtos e serviços (P&S), valor da proposta em recorrências (MRR), parcelas de pagamento, método de pagamento, status, data de validade e de vencimento, data de primeiro pagamento de parcela de recorrência (MRR), dados de transportadora e local de entrega triangular, marcas, valores dos campos customizados vinculados, etc.;
Dados da ação automática: data de disparo, nome do funil e etapa onde a ação automática foi executada, tipo de gatilho de acionamento da ação automática, usuário que disparou a ação automática (caso tenha sido realizada manualmente).

CONFIGURANDO A AÇÃO AUTOMÁTICA
1. Posicione o mouse sobre o avatar da conta, localizado no canto superior direito da tela. Clique em "Ajustes e configurações".
2. A seguir, clique em "Ações automáticas", na coluna Ferramentas.
3. Nesta tela, aparecerão listadas todas as ações automáticas cadastradas na conta. Para configurar uma nova ação automática, clique em "+ Adicionar ação".
4. Nesta primeira etapa será configurada a ação automática, bem como o gatilho que será responsável por executá-la.
Preencha os campos disponíveis com os seguintes dados: 
1.1. Nome da ação automática, para que seja fácil identificar esta Ação Automática na listagem;
1.2. Qual será o gatilho para ocorrer a ação?: informe qual o gatilho deve ser acionado para que a ação seja executada;
Dica: caso sejam selecionados gatilhos ao preencher formulários, será necessário selecionar o formulário que deverá ser preenchido para que a ação ocorra.
Caso selecione o gatilho  "ao adicionar uma tag na oportunidade" será necessário informar a tagque deve ser adicionada para disparar a ação automática.
1.3. Qual o tipo de ação que deseja realizar?: escolha a opção "Webhook - Envio de oportunidade para URL";
1.4. Defina a prioridade da execução da ação: informe a prioridade que a Ação Automática terá para ser enviada para a fila de execução;
1.5. Agendar execução da ação (Opcional): defina uma faixa de dia e horário para executar a ação. Caso a ação seja acionada fora dessa faixa, ela será automaticamente agendada para o próximo dia e horário disponível;
1.6. Quanto tempo após o gatilho a ação será enviada para fila de execução? (Opcional): defina o intervalo de tempo entre o momento em que o gatilho é disparado e a ação é enviada para a fila para ser executada (por exemplo, 5 minutos após a oportunidade entrar na etapa).
Dica: os tempos de espera variam entre "Imediatamente", quando a ação é enviada para execução no momento em que o gatilho é disparado, sem atraso, ou um tempo "Personalizado", onde é possível definir um período de espera em minutos, horas ou dias, com limite máximo de 7 dias de espera.
5. Na segunda etapa das configurações será necessário definir quais as condições das oportunidades para que a ação seja disparada.
2.1. Neste Funil (obrigatório): informe onde a Ação Automática poderá ser realizada;
2.2. Nesta etapa do funil (obrigatório): selecione em qual etapa Ação automática deve ser executada;
2.3. Com este status e situação (obrigatório):  selecione o Status e Situação que a Oportunidade poderá estar para executar essa Ação;
2.4. Origem das oportunidades: defina a origem das oportunidades para que a ação seja executada;
2.5. Tags das oportunidades, empresas e pessoas: selecione quais tags devem ser atribuídas às oportunidades, empresas ou pessoas para que a ação seja  executada;
2.6. Segmento das empresas: informe qual o segmento a empresa deve estar vinculada para que ação seja executada;
2.7. Neste horário de ativação do gatilho (Opcional): se esta opção estiver ativada, o gatilho só será acionado dentro das faixas de horários indicados. Será necessário configurar as faixas de horários para execução.

Clique em "Próximo".

6. A partir deste ponto, será configurada a segunda parte da ação automática. Informe os seguintes campos: 
Header: informação referente à chave que estará contida no cabeçalho, a depender da integração de destino;
Valor: valor referente à chave do header;

URL: campo de preenchimento obrigatório. Trata-se do endereço que receberá os dados enviados via webhook do CRM de vendas PipeRun;
Tipo de envio: é possível selecionar entre as opções "padrão" e "avançado", conforme abaixo:
Padrão: utiliza as chaves padrões do sistema da PipeRun, sem possibilidade de customização;
Avançado: possibilita customizar a formatação do JSON de saída dos dados enviados via ação automática.

Após preencher os campos necessários, clique em "Salvar".

Pronto! Após seguir os passos acima, sua ação automática de enviar para webhook estará configurada. 

CONFIGURANDO A FORMATAÇÃO DE SAÍDA DOS DADOS

Para customizar a formatação do JSON de saída dos dados enviados via ação automática, siga o passo a passo abaixo:

1. Em "Tipo de envio", na tela de edição da ação automática, selecione a opção "Avançado".
2. Em "Formatação de saída", selecione a opção "JSON". 
3. Na área "JSON Disponível" você poderá visualizar a estrutura padrão do JSON enviado via ação automática.
4. Já na área central da tela, configure a expressão de saída desejada, seguindo esta estrutura:
Caso de uso: a chave padrão do PipeRun referente ao nome fantasia do registro do contato da empresa é "company" e a chave padrão no sistema que receberá os dados via ação automática é "empresa". Para que a expressão seja recebida da forma correta no sistema de destino, basta configurar a expressão da seguinte forma:
{
"nome-da-chave-de-saída":"nome-da-chave-do-piperun"
}
Desta forma, a formatação do JSON enviado pelo PipeRun terá como chave "empresa", ao invés de "company".
{
  "empresa": "company"
}  
5. Agora, confira a pré-visualização da formatação do JSON de saída, na área direita da tela.
6. Para realizar um teste de envio de dados, basta clicar no botão "Envio de teste", localizado no canto inferior esquerdo da tela.
Dica: o envio de teste será realizado para a URL definida na ação automática.
7. Por fim, após finalizar as configurações de saída da ação automática, não esqueça de clicar no botão "Salvar", localizado no canto inferior direito da tela, para registrar a ação.
Dica: é possível ativar os logs temporários da ação automática. Para isto, clique nos logs na listagem de ações automáticas e, após em "Logs temporários". Os logs serão mantidos por 24 horas (após esse período, serão excluídos).

CÓDIGO COM A DESCRIÇÃO DAS CHAVES DISPONÍVEIS

{
  "id": 1234, // ID da oportunidade
  "title": "Oportunidade Exemplo", // Título da oportunidade
  "created_at": "2021-01-01 14:48:02", // Data da criação da oportunidade
  "closed_at": null, // Data de fechamento da oportunidade
  "probably_closed_at": null, // Data de previsão de fechamento da oportunidade
  "last_contact": null, // Data de último contato da oportunidade
  "description": null, // Descrição da oportunidade
  "observation": null, // Observação da oportunidade
  "status": 0, // Status da oportunidade (0 = Aberto / 1 = Ganha / 3 = Perdida)
  "deleted": 0, // Situação Lixeira da oportunidade (0 = Fora da lixeira / 1 = Na lixeira)
  "freezed": 0, // Situação Congelada da oportunidade (0 = Não congelada / 1 = Congelada)
  "frozen_at": null, // Data em que a oportunidade foi congelada
  "value": "0.00", // Valor total da oportunidade em produtos e/ou serviços (P&S)
  "value_mrr": "0.00", // Valor total da oportunidade em recorrência (MRR)
  "hash": "abc123", // Hash da oportunidade (obtida através da exportação)
  "order": 1, // Ordem numérica na listagem da etapa do funil
  "probability": null, // Probabilidade de fechamento da oportunidade
  "updated_at": null, // Data da última alteração da oportunidade
  "stage_changed_at": null, // Data da última mudança de etapa da oportunidade
  "lead_time": null, // Lead time da oportunidade (tempo entre a criação da oportunidade e seu fechamento)
  "temperature": null, // Temperatura da oportunidade (1= Muito quente / 2= Quente / 3= Morna / 4= Fria)
  "lost_reason": { // Motivo de perda da oportunidade
      "id": null, // ID do motivo de perda
    "name": null, // Nome do motivo de perda
    "description": null // Descrição do motivo de perda da oportunidade
  },
  "company": { // Array contendo dados da empresa da oportunidade
      "id": 1234, // ID da empresa
    "ie": null, // Inscrição estadual da empresa
    "name":"abc123", // Nome fantasia da empresa
    "cnpj": null, // CNPJ da empresa
    "hash": "abc123", // Hash única da empresa
    "logo": null,  // Logo da empresa
    "open_at": "2000-01-01", // Data de fundação da empresa
    "country": null, // País da empresa
    "website": null, // Website da empresa
    "email_nf": null, // E-mail para envio de nota fiscal da empresa
    "created_at":"2017-01-01 22:11:59", // Data de cadastro da empresa
    "observation": null, // Observações da empresa
    "company_name": null,  // Razão social da empresa
    "address": { // Array contendo dados de endereço da empres
        "street": null, // Nome da rua do endereço da empresa
      "postal_code": null, // CEP do endereço da empresa
      "number": null, // Número do endereço da empresa
      "complement": null, // Complemento do endereço da empresa
      "district": null // Bairro do endereço da empresa
    },
    "size": null, // Tamanho da empresa
    "cnae": "123", // Codigo do CNAE primário da empresa
    "facebook": null, // URL da página do Facebook da empresa
    "linkedin": null, // URL da página do Linkedin da empresa
    "status_touch": null, // Modelo touch da empresa (1= High touch / 2= Middle touch / 3= Low touch / 4= Tech touch / 5= Now touch)
    "company_situation": "null", // Situação da empresa (1= Lead / 2= Lead Suspect / 3= Lead Qualificado Marketing (MQL) / 4= Lead Qualificado Vendas (SQL) / 5= Lead Aceito Vendas (SAL) / 6= Lead Prospects (OPs) / 7= Cliente ativo / 8= Cliente inativo / 9= Cliente perdido (Churn))
    "city": { // Array contendo dados cidade e estado da empresa
      "id": 1234, // ID da cidade da empresa
      "name": "abc", // Nome da cidade da empresa
      "uf": "abc" // UF da cidade da empresa
    },
    "segment": { // Array contendo os dados do segmento da empresa
      "id": 1234, // ID do segmento da empresa
      "name": "abc", // Nome do segmento da empresa
      "sector": null // Setor do segmento da empresa
    },
    "user": { 
      "id":1234, // ID do usuário
      "name":"abc" // Nome do usuário
    },
    "cnaes": [{ // Array contendo os dados de CNAE da empresa
      "id": 1234, // ID do CNAE da empresa da oportunidade
      "code": "123", // Código do CNAE da empresa da oportunidade
      "description": "abc123" // Descrição do CNAE da empresa da oportunidade
    }],
    "contact_emails": [{ // Array contendo os dados de e-mail da empresa
      "id": 1234, // ID do e-mail de contato da empresa da oportunidade
      "address": "teste@email.com" // Endereço do e-mail de contato da empresa da oportunidade
    }],
    "contact_phones": [{ // Array contendo os dados de telefone da empresa
      "id": 1234, // ID do telefone de contato da empresa
      "number": "123" // Número do telefone de contato da empresa
    }],
    "economic_groups": [{ // Array contendo informações do grupo econômico vinculado à empresa
    "id": 123, // ID do grupo econômico vinculado à empresa
    "name": "abc123" // Nome do grupo econômico vinculado à empresa
    }],
    "fields": [{ // Array contendo informações dos campos customizados vinculados a empresa
      "id": 1234, // ID do campo customizado
      "nome": "abc123", // Nome do campo customizado
      "valor": null, // Valor do campo customizado
      "tipo": 1, // Tipo do campo customizado (1= Texto / 3= Única escolha / 5= Texto longo / 6= Múltipla escolha / 7= Chat do Skype / 8= Chamada do Skype / 9= Cadastro de link / 10= Data / 11= Número de WhatsApp / 12= Cidade / 13= Fórmula / 14= Numérico / 15= Upload de arquivo)
      "valores": null 
     }],
      "forms": [] // Formulários vinculados à empresa da oportunidade 
  },
  "person": {
    "id": 1234,  // ID da pessoa de contato vinculada a oportunidade
    "hash": "abc123", // Hash da pessoa vinculada a oportunidade
    "name": "abc123", // Nome da pessoa vinculada a oportunidade
    "cpf": null, // CPF da pessoa vinculada a oportunidade
    "job_title": "abc123", // Cargo da pessoa vinculada a oportunidade
    "rdstation": "URL public do rdstation",  // Link do RDStation da pessoa vinculada a oportunidade
    "birth_day": null, // Data de nascimento da pessoa vinculada a oportunidade
    "gender": null, // Gênero da pessoa vinculada a oportunidade
    "website": null, // URL do website da pessoa vinculada a oportunidade
    "facebook": null, // URL do perfil do Facebook da pessoa vinculada a oportunidade
    "linkedin": null, // URL do perfil do Linkedin da pessoa vinculada a oportunidade
    "avatar": null, // Avatar da pessoa vinculada a oportunidade
    "observation": null, // Observações da pessoa vinculada a oportunidade
    "address": { // Array contendo endereço da pessoa vinculada a oportunidade
      "street": null, // Nome da rua do endereço da pessoa vinculada a oportunidade
      "postal_code": null, // CEP do endereço da pessoa vinculada a oportunidade
      "number": null, // Número do endereço da pessoa vinculada a oportunidade
      "complement": null, // Complemento do endereço da pessoa vinculada a oportunidade
      "district": null // Bairro do endereço da pessoa vinculada a oportunidade
    },
    "company": { // Array contendo os dados da empresa vinculada à pessoa da oportunidade
      "id": 1234, // ID da empresa vinculada a pessoa da oportunidade
      "name": "123", // Nome da empresa vinculada a pessoa da oportunidade
      "cnae": "123", // CNAE da empresa vinculada a pessoa da oportunidade
        "fields": [{ // Array contendo dados do campo customizado
          "id": 1234, // ID do campo customizado
          "nome": "abc123", // Nome do campo customizado
          "valor": "abc123", // Valor do campo customizado
          "tipo": 1, // Tipo do campo customizado (1= Texto / 3= Única escolha / 5= Texto longo / 6= Múltipla escolha / 7= Chat do Skype / 8= Chamada do Skype / 9= Cadastro de link / 10= Data / 11= Número de WhatsApp / 12= Cidade / 13= Fórmula / 14= Numérico / 15= Upload de arquivo)
          "valores": null
      }]
    }
  },
  "tags":[{ // Arrays contendo informações sobre as tags da portunidade
         "id": 1223, // ID da tag
         "name": "abc123", // Nome da tag
         "color": "warning" // Cor da tag
      }
    ],
  "stage": {
      "id": 1234, // ID da etapa do funil da oportunidade
    "name": "abc123" // Nome da etapa do funil da oportunidade
  },
  "pipeline": {
    "id": 1234, // ID do unil em que se encontra a oportunidade
    "name":"abc123" // Nome do funil em que se encontra a oportunidade
  },
  "origin":{ 
    "id": 1234, // ID da origem da oportunidade
    "name": null, // Nome da origem da oportunidade
    "origin":{
      "id": null,  // ID da origem em caso de sub-origem da oportunidade
      "name": null  // Nome da sub-origem da oportunidade
    }
  },
  "user": { 
    "id": 1, // ID do usuário dono da oportunidade
      "name": "abc", // Nome do usuário dono da oportunidade
      "avatar": null // Foto do usuário dono da oportunidade
  },
"involved_users": [
    {
      "id": 123, //ID do usuário envolvido na oportunidade
      "name": "John Doe", //Nome do usuário envolvido na oportunidade
      "email": "teste@email.com", //E-mail do usuário envolvido na oportunidade
      "telephone": "(20) 7042-5594", //Telefone do usuário envolvido na oportunidade
      "cellphone": "(73) 9 8586-5687", //celular do usuário envolvido na oportunidade
      "involvement": {
        "deal_involved_role": "teste", //Papel do usuário envolvido na oportunidade
        "proposal_involved_role": "Testador" //Nome do papel na proposta do usuário envolvido na oportunidade
      }
    }
  ],
  "city": {
    "id": null,  // ID da cidade da oportunidade
    "name": null,  // Nome da cidade da oportunidade
    "uf": null  // UF da cidade da oportunidade
  },
  "proposals": [{ // Array contendo informações da proposta
    "id": 123, // ID da proposta
    "hash": "123abc", // Hash da proposta
    "value": "00,00", // Valor da proposta em produtos e/ou serviços (P&S)
    "status": 0, // Status da proposta (0= Aberta / 1= Aprovada / 2= Negada / 3= Cancelada / 4= Aguardando assinatura / 5= Assinada)
    "created_at": "2021-07-14T14:32:39.000000Z", // Data de criação da proposta
    "valid_until": "2021-07-21", // Data de validade da proposta (formato YYYY-MM-DD)
    "payment_method_mrr": null, // Forma de pagamento recorrência (MRR)
    "value_mrr": "0.00", // Valor da proposta em recorrência (MRR)
    "due_date_mrr": null, // Data de vencimento da recorrência (MRR)
    "first_payment_mrr_date": null, // Data do primeiro pagamento da recorrência (MRR)
    "user":{ // Usuário responsável pela proposta
      "id": 1234, // ID do usuário responsável pela proposta
      "name":"xxxxxx" // Nome do usuário responsável pela proposta    
    },
    "shippingCompany": [], // Dados da transportadora
    "billingCompany": [], // Dados da empresa de faturamento
    "triangularCompany": [], // Empresa de entrega triangular
    "brand":{ // Dados da empresa (marca)
      "id":1, // ID da empresa (marca)
      "ie": null, // Inscrição estadual da empresa (marca)
      "name":"abc", // Nome fantasia da empresa (marca)
      "cnpj": null, // CNPJ da empresa (marca)
      "hash": "123abc", // Hash da empresa  (marca)
      "logo": null,  // Logo da empresa  (marca)
      "open_at": "2000-01-01", // Data de fundação da empresa  (marca)
      "website": null, // Website da empresa  (marca)
      "email_nf": null, // E-mail para envio da nota fiscal  (marca)
      "created_at": "2017-01-01 22:11:59", // Data de cadastro da empresa  (marca)
      "observation": null, // Observações da empresa  (marca)
      "company_name": null,  // Razão social da empresa (marca)
      "address":{ // Array contendo dados de endereço da empresa  (marca)
        "street": null, // Nome da rua do endereço da empresa  (marca)
        "postal_code": null, // CEP do endereço da empresa  (marca)
        "number": null, // Número do endereço da empresa  (marca)
        "complement": null, // Complemento do endereço da empresa  (marca)
        "district": null // Bairro do endereço da empresa  (marca)
      },
      "size": null, // Tamanho da empresa  (marca)
      "cnae": "123", // Código do CNAE primário da empresa  (marca)
      "facebook": null, // URL da página do Facebook da empresa  (marca)
      "linkedin": null, // URL da página do Linkedin da empresa  (marca)
      "status_touch": null, // Modelo touch da empresa  (marca) (1= High touch / 2= Middle touch / 3= Low touch / 4= Tech touch / 5= Now touch)
      "company_situation": "Cliente ativo" // Situação da empresa (marca)
    },
    "items": [{
      "id": 1234, // ID do item (P&S / MRR)
      "name": "abc123", // Nome do item (P&S / MRR)
      "reference": null, // Referência do item (P&S / MRR)
      "code": null, // Código do item (P&S / MRR)
      "category": null, // Categoria do item (P&S / MRR)
      "characteristics": [], // Caracteristicas do item (P&S / MRR) na proposta
      "type": 1, // Tipo de item (0= Produto / 1= MRR / 2= Serviço)
      "cost": null, // Custo do item (P&S / MRR) na proposta se não do cadastro
      "value": 1234, // Valor do item (P&S / MRR) na proposta
      "ipi_tax": 1234, // Taxa de IPI aplicada no item (P&S / MRR) na proposta
      "quantity": 1, // Quantidade de itens na proposta
      "discount": 1.5, // Desconto aplicado na proposta para o item (P&S / MRR)
      "commission_final_value": 0, // Valor total de comissão do item (P&S / MRR) na proposta
      "type_of_charge": 2, // Em caso de item do tipo recorrência (MRR), é o tipo de cobrança para pagamento
      "charge_name": "Mensal", // Nome do tipo de cobrança
      "duration": 12, // Em caso de item do tipo recorrência (MRR), duração do produto
      "contract_start_at": null, // Em caso de item do tipo recorrência (MRR), , data de início da duração do contrato
      "contract_end_at": null,// Em caso de item do tipo recorrência (MRR), data final da duração do contrato
      "comissao": null,
      "description": "abc123", // Descrição do item na proposta
      "discount_type": 0, // Tipo de desconto aplicado (0= Percentual / 1= Absoluto)
      "fix_commission_value": null, // Valor absoluto da comissão aplicada
      "commission_incidence_type": 1, // Em caso de item do tipo recorrência (MRR), tipo de incidencia de comissão (1= 1ª mensalidade / 2= 12 primeiras mensalidades / 3= 1ª e última mensalidade / 4= todas as mensalidades)
      "commission_incidence_name": "1ª mensalidade" // Em caso de item do tipo recorrência (MRR), nome de incidência de comissão
      }
    ],
    "parcels": [{ // Arrays contendo as informações das parcelas de produtos e serviços (P&S)
      "id": "00", // ID da parcela de P&S
      "value": "0.0000", // Valor da parcela  de P&S
      "parcel": "0", // Tipo de parcela  de P&S
      "discount": null, // Desconto  de P&S
      "commission_final_value": 0, // Valor total da comissão referente a parcela de P&S
      "due_date": "2018-01-01", // Data de vencimento da parcela  de P&S
      "account_id": "1", // ID da conta
      "proposal_id": "000", // ID da proposta
      "payment_method_type_id": { // ID do tipo de método de pagamento
        "id": "1", // ID do método de pagamento
        "name": "abc" // Nome do método de pagamento
        }
     }],
    "parcels_mrr": [{ // // Arrays contendo as informações das parcelas de recorrência (MRR)
      "id": 894879, // ID da parcela de recorrência (MRR)
      "value": "51.2200", // Valor da parcela de recorrência (MRR)
      "parcel": "1", // Número da parcela de recorrência (MRR)
      "discount": null, // Desconto aplicado na parcela de recorrência (MRR)
      "commission_final_value": "0.00", // Valor real da comissão aplicada na parcela de recorrência (MRR)
      "due_date": "2022-07-28", // Data de vencimento da recorrência (MRR)
      "account_id": 9552, // ID da conta
      "proposal_id": 500597, // ID da proposta
      "payment_method_type_id": { // ID do tipo de método de pagamento
        "id": 11, // ID do método de pagamento
        "name": "PIX" //  Nome do método de pagamento
      }
    }]
  }],
  "activities": [], // Atividades que estão vinculadas a oportunidade
  "files": [],
  "fields": [{// Campos customizados da oportunidade
    "id": 1234, // ID do campo customizado da oportunidade
    "nome": "abc123", // Nome do campo customizado da oportunidade
    "valor": null, // Valor do campo customizado da oportunidade
    "tipo": 6, // Tipo de campo customizado
    "valores":"[\"abc123\",\"abc123\",\"abc123\"]" // Valores utilizados nos campos de seleção
  }],
  "forms": [], // Formulários vinculados com a oportunidade
  "action": { // Dados da ação automática do PipeRun
    "create": "2021-07-13T17:48:06.816034Z", // Data do disparo da ação automática
    "pipeline":"abc123", // Nome do funil que foi disparada a ação automática
    "stage": "Qualquer etapa", // Nome da(s) etapa(s) que foi disparada a ação automática
    "trigger_type": "Uma oportunidade entrar na etapa selecionada", // Tipo de gatilho da ação automática
    "user": null // Usuário que disparou a ação, caso tenha sito disparada manualmente
   }
}

PERGUNTAS FREQUENTES

1. É possível enviar e receber payload pelo webhook no CRM de vendas?

R: Sim, é possível enviar através da ação automática de envio por URL e receber através do integrador Json

2. É possível enviar o histórico de um lead convertido utilizando a ação automática de envio por Webhook?
R: Não é possível enviar o histórico da oportunidade em si. Entretanto, o sistema permite incluir dados sobre a empresa, a oportunidade e as pessoas relacionadas. Além disso, ao configurar a ação automática no modo “Avançado”, é possível personalizar o JSON de saída com as informações que se deseja enviar.

3. É possível usar um valor fixo em uma variável no webhook que não esteja associado aos campos do Piperun?

R: Sim, é possível sim enviar dados estáticos que não são do CRM PipeRun.

Abaixo irei repassar dois exemplos de como enviar as informações:

I - Envio de String (texto): basta adicionar os parâmetros em aspas duplas e valores em aspas simples (em outros casos, aspas duplas), ficando desta maneira:"fk_journey": '451'

Dica: neste exemplo o valor é repassado como Texto. 

II - Envio de Valores Inteiros (e valores com virgula): basta que o valor do parâmetros seja escrito com a crase, ficando da seguinte maneira:"fk_journey": `451`

Dica: neste exemplo, o valor é repassado como valor numérico. 

Como configurar uma integração via JSON
O JSON (JavaScript Object Notation) é um formato de troca de dados simples entre sistemas, processo também conhecido como parsing. Através de uma configuração de troca de dados, é possível integrar sistemas web, sites ou formulários em geral ao PipeRun. 

Por exemplo: você pode configurar o envio de dados coletados através de uma landing page diretamente para o CRM de vendas PipeRun e gerar uma nova oportunidade, de forma automática.

Atenção! Este artigo exige conhecimentos técnicos em programação, tendo em vista que será necessário configurar as regras de envio de dados da integração em um JSON e inserir um script na página da landing page.

Confira neste artigo como realizar a configuração da integração. Também disponibilizamos uma explicação em vídeo, logo abaixo.

CONFIGURANDO UMA INTEGRAÇÃO JSON
Informações gerais para envio de dados:

Endpoint: https://app.pipe.run/webservice/integradorJson?hash=HASH_DA_ETAPA_DO_FUNIL
Tipo de requisição: POST
Tipo de conteúdo (Content-Type): application/json
É necessário ter o ID do formulário a ser integrado e o código precisa ser adaptado à realidade da integração. 

JSON DE REGRAS (ENVIO NÃO OBRIGATÓRIO)
Confira abaixo o código, juntamente com a descrição, contendo as regras do envio dos dados:

{
    "rules": {
    
        "update": "informe_aqui_o_valor",
        /*
         * NOME: UPDATE 
         * DESCRICAO: DETERMINA SE IRA CRIAR OU ATUALIZAR UMA OPORTUNIDADE
         * VALORES: {
         *  true, // ATUALIZARÁ UMA OP, UTILIZANDO O ATRIBUTO 'ID' DO LEAD
         *  false // CRIARÁ UMA NOVA OP
         * }
         * PADRÃO: false
         */
         
        "filter_status_update": "informe_aqui_o_valor",
        /*
         * NOME: FILTER_STATUS_UPDATE 
         * DESCRICAO: SE JÁ HOUVER UMA OPORTUNIDADE COM O STATUS INFORMADO(E COM O MESMO ID), SERÁ ATUALIZADA ESSA OPORTUNIDADE, CASO CONTRARIO, UMA NOVA SERÁ CRIADA.
         * VALORES: {
         *  "open", // ABERTA
         *  "closed", // GANHA
         *  "lost" // PERDIDA
         * }
         * PADRÃO: "open" // ABERTA
         */
         
        "filter_situation_update": "informe_aqui_o_valor",
        /*
         * NOME: FILTER_SITUATION_UPDATE 
         * DESCRICAO: SE JÁ HOUVER UMA OPORTUNIDADE COM A SITUAÇÃO INFORMADA(E COM O MESMO ID), SERÁ ATUALIZADA ESSA OPORTUNIDADE, CASO CONTRARIO, UMA NOVA SERÁ CRIADA..
         * VALORES: {
         * "unfreezed",   // NORMAL ou descongelada
         * "freezed",  //  CONGELADA
                 * "undeleted",  // NORMAL ou fora da lixeira
         * "deleted"  // NA LIXEIRA
         * }
         * PADRÃO: "NULL" // NULO ou oportunidade aberta normal.
         */
        
        "equal_pipeline": "informe_aqui_o_valor",
         /*
         * NOME: EQUAL_PIPELINE
         * DESCRICAO: DETERMINA O FUNIL PARA BUSCAR A OP POR SER ATUALIZADA
         * VALORES: {
         *  true, // BUSCARÁ A OP NO MESMO FUNIL DA ETAPA RECEBIDA ATRAVÉS DA HASH
         *  false // BUSCARÁ A OP INDEPENDENTE DO FUNIL
         * }
         * PADRÃO: false
         * OBS:DEPENDE DA REGRA 'UPDATE' > 'TRUE'
         */
        
        
        "status": "informe_aqui_o_valor",
          /*
         * NOME: STATUS
         * DESCRICAO: DETERMINA O STATUS DA OPORTUNIDADE
         * VALORES: {
         *  "open", // ABERTA
         *  "closed", // GANHA
         *  "lost" // PERDIDA
         * }
         * PADRÃO: "open"
         * OBS: AÇÕES AUTOMÁTICAS SERÃO EXECUTADAS, DEPENDENDO DO GATILHO
         */
        
        
        "situation": "informe_aqui_o_valor",
        
        /*
         * NOME: SITUATION
         * DESCRICAO: DETERMINA A SITUAÇÃO DA OPORTUNIDADE
         * VALORES: {
         *  "freezed", // CONGELA A OP
         *  "unfreezed", // DESCONGELA A OP
         *  "delete", // ENVIA PARA LIXEIRA A OP
         *  "undelete", // RETIRA DA LIXEIRA A OP
         * }
         * PADRÃO: "NULL" // NULO
         * OBS: PERMITE MAIS DE UM VALOR NESSA REGRA, DEVENDO SER SEPARADO POR "," (VÍRGULA)
         */
        
        
        "validate_cpf": "informe_aqui_o_valor",
        /*
         * NOME: VALIDATE_CPF
         * DESCRICAO: DETERMINA BUSCA DA PESSOA
         * VALORES: {
         *  true, // BUSCA A PESSOA ATRAVÉS DO ATRIBUTO 'CPF'
         *  false // BUSCA A PESSOA ATRAVÉS DO ATRIBUTO 'EMAIL'
         * }
         * PADRÃO: false
         */
        
        
        "validate_cnpj": "informe_aqui_o_valor"
        /*
         * NOME: VALIDATE_CNPJ
         * DESCRICAO: DETERMINA BUSCA DA EMPRESA
         * VALORES: {
         *  true, // BUSCA A EMPRESA ATRAVÉS DO ATRIBUTO 'CNPJ'
         *  false // BUSCA A EMPRESA ATRAVÉS DO ATRIBUTO 'COMPANY'
         * }
         * PADRÃO: false
         */
        
        
        "validate_person_phone": "informe_aqui_o_valor"
        /*
         * NOME: VALIDATE_PERSON_PHONE
         * DESCRICAO: DETERMINA BUSCA DA PESSOA PELO TELEFONE
         * VALORES: {
         *  true, // BUSCA A PESSOA ATRAVÉS DOS ATRIBUTOS 'PERSON_PHONE_MAIN, PERSONAL_PHONE, MOBILE_PHONE'
         *  false // BUSCA A PESSOA ATRAVÉS DO ATRIBUTO 'EMAIL'
         * }
         * PADRÃO: false
         */
    }
}
JSON DO LEAD (ENVIO OBRIGATÓRIO)

Confira abaixo o código, juntamente com a descrição, do envio dos dados do lead.

{
    "leads": [{
        "id": "informe_aqui_o_valor", // OPORTUNIDADE: IDENTIFICADOR (PARA NÃO DUPLICAR)
        "user": "informe_aqui_o_valor", // OPORTUNIDADE: DONO
        "title": "informe_aqui_o_valor", // OPORTUNIDADE: TITULO
        "value": "informe_aqui_o_valor", // OPORTUNIDADE: VALOR DA NEGOCIAÇÃO
        "value_mrr": "informe_aqui_o_valor", // OPORTUNIDADE: VALOR RECORRENTE
        "email": "informe_aqui_o_valor", // PESSOA: E-MAIL
        "name": "informe_aqui_o_valor", // PESSOA: NOME
        "cpf": "informe_aqui_o_valor", // PESSOA: CPF
        "birth_day": "informe_aqui_o_valor", // PESSOA: DATA DE NASCIMENTO
        "person_phone_main": "informe_aqui_o_valor", // PESSOA: TELEFONE PRINCIPAL
        "personal_phone": "informe_aqui_o_valor", // PESSOA: TELEFONE
        "mobile_phone": "informe_aqui_o_valor", // PESSOA: TELEFONE
        "public_url": "informe_aqui_o_valor", // PESSOA: URL PÚBLICO
        "job_title": "informe_aqui_o_valor", // PESSOA: CARGO
        "cnpj": "informe_aqui_o_valor", // EMPRESA: CNPJ
        "company_website":"informe_aqui_o_valor", // EMPRESA: WEBSITE 
        "company": "informe_aqui_o_valor", // EMPRESA: NOME
        "company_phone_main": "informe_aqui_o_valor", // EMPRESA: TELEFONE PRINCIPAL
        "city_name": "informe_aqui_o_valor", // CIDADE: NOME (UTILIZADO EM EMPRESA/PESSOA)
        "city_state": "informe_aqui_o_valor", // CIDADE: UF (UTILIZADO EM EMPRESA/PESSOA)
        "last_conversion": {
            "source": "informe_aqui_o_valor" // OPORTUNIDADE: ORIGEM
        },
        "custom_fields": {
            "Nome do campo 1": "informe_aqui_o_valor", // OPORTUNIDADE: CAMPO CUSTOMIZADO
            "Nome do campo 2": "informe_aqui_o_valor" // OPORTUNIDADE: CAMPO CUSTOMIZADO
        },
        "tags": [
            "nome_da_tag 1", // OPORTUNIDADE: TAG
            "nome_da_tag 2" // OPORTUNIDADE: TAG
        ],
        "notes": [
            "texto da nota 1", // OPORTUNIDADE: NOTAS
            "texto da nota 2" // OPORTUNIDADE: NOTAS
        ]
    }]
}
EXEMPLO DO JSON DE REGRAS E LEAD
{
    "rules": {
        "update": true,
        "equal_pipeline": false,
        "validate_cpf": true,
        "validate_cnpj": true,
        "validate_person_phone": true,
        "situation": "freezed,undelete",
        "status": "open",
        "filter_status_update": "open",
        "filter_situation_update": "freezed"
    },
    "leads": [{
        "user": "user@teste.com",
        "id": "pessoa_email@email.com",
        "title": "Teste OP via Json",
        "name": "Pessoa 123",
        "email": "pessoa_email@email.com",
        "cpf": "0123456789",
        "person_phone_main": "(51) 33333334", 
        "personal_phone": "(51) 33333333",
        "mobile_phone": "(51) 999999999",
        "company": "Empresa 456",
        "cnpj": "9876543210",
        "company_website:"https://meusite.com.br"
        "company_phone_main": "(51) 33333334",
        "city_name": "Porto Alegre",
        "city_state": "RS",
        "last_conversion": {
            "source": "Formulário Site"
        },
        "custom_fields": {
            "Campo customizado teste 1": "Sim",
            "Campo customizado teste 2": "(51) 123456789"
        },
        "tags": [
            "Tag teste 1",
            "Tag teste 2"
        ],
        "notes": [
            "Lead obtido através da integração com o formulário XYZ do site ABC."
        ]
    }]
}
Pontos importantes: 

Caso o título não seja definido, o padrão H:i d/m Integração: Name será utilizado;
Caso as tags informadas não existam, elas serão criadas e vinculadas, automaticamente;
Caso os campos customizados não existam, eles serão criados e vinculados automaticamente (serão campos do tipo "texto" por padrão. Para outros tipos de campos, o campo deverá ser criado previamente);
 Você poderá utilizar o atributo id para evitar a duplicidade de leads, utilizando o dado informado para ser único junto a oportunidade. Dessa forma, antes de salvar, o PipeRun irá verificar se esse dado já existe vinculado a alguma oportunidade aberta, e, existindo o dado, será apenas atualizada a oportunidade, caso contrário, uma nova oportunidade será criada;
É possível ainda atualizar oportunidades com status diferente de aberta, como perdida, ou ganha utilizando o parâmetro filter_status_update nas rules;
Também é possível atualizar oportunidades que estejam na situação de congelada ou na lixeira, utilizando o parâmetro filter_situation_update.
O PipeRun devolverá um JSON com status de sucesso, caso tudo tenha funcionado, após realizar o cadastro:

{
  "success": true,
  "message": "OK",
  "data": {
    "id": [
      "1234"
    ],
    "hash": [
      "qkh5ei2qjkvvh9302yhp44zmtlyzxkz"
    ]
  }
}
OUTROS EXEMPLOS

Exemplo de código para captura de lead via integrador JSON com coleta de campanha através das variáveis UTM e conversão no Analytics.

// METODO PARA COLETAR DADOS DO FORMULARIO E ENVIAR PARA O PIPERUN, COM DADOS DE UTM.
// dependencia do script sessionStart.min.js
// dependencia do google analytics.


// sessionStart.min.js
function setCookie(name,value,exdays=1){var expires;var date;var value;date=new Date();date.setTime(date.getTime()+(exdays*24*60*60*1000));expires=date.toUTCString();document.cookie=name+"="+value+"; expires="+expires+"; path=/"}function getCookie(name){var c_name=document.cookie;if(c_name!=undefined&&c_name.length>0){var posCookie=c_name.indexOf(name);if(posCookie>=0){var hashOportunidade='';var value="; "+document.cookie;var parts=value.split("; "+name+"=");if(parts.length==2){hashOportunidade=parts.pop().split(";").shift()}return hashOportunidade}else{return!1}}}function eraseCookie(name){setCookie(name,-1)}function getRequestURL(name){if(name=(new RegExp('[?&]'+encodeURIComponent(name)+'=([^&]*)')).exec(location.search))return decodeURIComponent(name[1])}function setSessionStart(sessionStartName,arTerms=['utm_source','utm_medium','utm_campaign','utm_term','utm_content','utm_position','utm_device','utm_match','utm_creative','plano','tipo']){arTerms.forEach(function(termo){var nameTermo=sessionStartName+'_'+termo;sessionStartCookie[termo]='';sessionStart[termo]='';var termValue=' ';var boSet=!1;if(getRequestURL(termo)){termValue=getRequestURL(termo);boSet=!0}if(!getCookie(nameTermo)){boSet=!0}if(boSet){setCookie(nameTermo,termValue)}termValue=getCookie(nameTermo);if(termValue==''){termValue=' '}sessionStartCookie[termo]=termValue;sessionStart[termo]=termValue})}var sessionStartCookie=new Object();var sessionStart=new Object();


// URL de referencia;
// https://crmpiperun.com/?utm_source=google&utm_medium=cpc&utm_campaign=institucional


function getParameterByName(name) {
    name = name.replace(/[\[]/, "\\[").replace(/[\]]/, "\\]");
    var regex = new RegExp("[\\?&]" + name + "=([^&#]*)"),
    results = regex.exec(location.search);
    if (sessionStart && sessionStart[name]) {
        return (sessionStart[name] === null || sessionStart[name] === "" || sessionStart[name] === " " || sessionStart[name] === undefined) ? "" : sessionStart[name];
    }


    return results === null ? "" : decodeURIComponent(results[1].replace(/\+/g, " "));
}


// Função para formatação de data.
function formatDate(date) {
    return (date.getDate() < 10 ? '0' : '') + date.getDate()
    + '/' + 
    (date.getMonth() + 1)
    + '/' + 
    date.getFullYear() + ' ' +
    date.getHours() + ':' + date.getMinutes();
}


const form = document.getElementById('conversion-form');
form.addEventListener('submit', enviarDados);


function enviarDados() {
      
    if ($("#conversion-form").validate().errorList.length) {
        return false;
    }


    // ENDPOINT
    let endpoint = "https://app.pipe.run/webservice/integradorJson?hash=aaaa0000-0a00-0a0a-0000-0aa000a00aa0"


    let dataHora = formatDate(new Date());
    let name = jQuery('#text_field-conversion-form').val();
    let email = jQuery('#email_field-conversion-form').val();
    let company = jQuery('#text_field-conversion-form').val();
    let phone = jQuery('#phone_field-conversion-form').val();


    let utm_source = getParameterByName('utm_source');
    let utm_medium = getParameterByName('utm_medium');
    let utm_campaign = getParameterByName('utm_campaign');
    let utm_term = getParameterByName('utm_term');
    let utm_content = getParameterByName('utm_content');
    let utm_position = getParameterByName('utm_position');
    let utm_device = getParameterByName('utm_device');
    let utm_match = getParameterByName('utm_match');
    let utm_creative = getParameterByName('utm_creative');


    // RULES
    let rules = {
        "update": true,
        "status": "open",
        "equal_pipeline": true,
        "filter_status_update": "open",
    }


    // LEAD
    let lead = [{
        "id": email,
        "title": dataHora + " " + company,
        "name": name,
        "email": email,
        "company": company,
        "mobile_phone": phone,
        "last_conversion": {
            "source" : "Site_PipeRun"
        },
        "custom_fields": {
            "url_conversao": location.href,
            "utm_source": utm_source,
            "utm_medium": utm_medium,
            "utm_campaign": utm_campaign,
            "utm_term": utm_term,
            "utm_content": utm_content,
            "utm_position": utm_position,
            "utm_device": utm_device,
            "utm_match": utm_match,
            "utm_creative": utm_creative
        },
        "notes" : [
            "Título: " + dataHora + " Fale Consultor CRM</br>" +
            "E-mail: " + email + "</br>" +
            "Nome: " + name + "</br>" +
            "WhatsApp: " + phone + "</br>" +
            "Empresa: "  + company + "</br>" +
            "utm_source: " + utm_source + "</br>" +
            "utm_medium: " + utm_medium + "</br>" +
            "utm_campaign: " + utm_campaign + "</br>" +
            "utm_term: " + utm_term + "</br>" +
            "utm_content: " + utm_content + "</br>" +
            "utm_position: " + utm_position + "</br>" +
            "utm_device: " + utm_device + "</br>" +
            "utm_match: " + utm_match + "</br>" +
            "utm_creative: " + utm_creative
        ]
    }]


    jQuery.each(lead[0], function(index, value) {
        if (typeof value == 'string' && value == '') {
            delete lead[0][index]
        }
    }); 


    // DATA
    let dataToSend = {
        "rules": rules,
        "leads": lead
    }
    jQuery.ajax({
        type: "post",
        data: JSON.stringify(dataToSend),
        dataType: "json",
        url: endpoint, 
        success: function(data) {
            ga('send','event','form','contato','fale-consultor');
        }
    });
}
Exemplo de código para captura de lead via integrador JSON com javascript puro, sem a necessidade de uso do jQuery.

// Integrador JSON PipeRun.


document.getElementById('button').onclick = function(event) {
    // ENDPOINT
    let endpoint = "https://app.pipe.run/webservice/integradorJson?hash=aaaa0000-0a00-0a0a-0000-0aa000a00aa0"


    // RULES
    let rules = {
        "update": true,
        "equal_pipeline": true,
        "filter_status_update": "open"
    }


    // LEAD
    let lead = [{
        "id": document.getElementsByName('email')[0].value,
        "title": "CRM PipeRun Landing Page",
        "user": "suporte@crmpiperun.com",
        "name": document.getElementsByName('name')[0].value,
        "email": document.getElementsByName('name')[0].value,
        "mobile_phone": document.getElementsByName('phone')[0].value,
        "last_conversion": {
            "source": "Site_CRMPipeRun"
        },
        "custom_fields": {
            "segmento": (document.getElementsByName('segmento')[0].value ? document.getElementsByName('segmento')[0].value : "Não Informado")
        },
        "tags": [
            "Contato"
        ],
        "notes": [
            "Contato enviado através do formulário de consultoria técnica do CRM PipeRun."
        ]
    }]


    // DATA
    let dataToSend = {
        "rules": rules,
        "leads": lead
    }


    // Requisição POST
    fetch(endpoint, {
        headers: {
            'Content-Type': 'text/plain'
        },
        method: "POST",
        body: JSON.stringify(dataToSend)
    }).then((response) => { 
        // Retorno do Ajax
        console.log(response);
        ga('send','event','form','contato','fale-consultor');
    }).catch((error) => { 
        console.log(error);
    });
};

Exemplo de código para captura de lead via integrador JSON implementado no Wordpress com Elementor.

// METODO PARA COLETAR DADOS DO FORMULARIO E ENVIAR PARA O PIPERUN, COM DADOS DE UTM.
// dependencia do google analytics.


// URL de referencia;
// https://crmpiperun.com/?utm_source=google&utm_medium=cpc&utm_campaign=institucional


function getParameterByName(name) {
    name = name.replace(/[\[]/, "\\[").replace(/[\]]/, "\\]");
    var regex = new RegExp("[\\?&]" + name + "=([^&#]*)"),
    results = regex.exec(location.search);


    return results === null ? "" : decodeURIComponent(results[1].replace(/\+/g, " "));
}


// Função para formatação de data.
function formatDate(date) {
    return (date.getDate() < 10 ? '0' : '') + date.getDate()
    + '/' + 
    (date.getMonth() + 1)
    + '/' + 
    date.getFullYear() + ' ' +
    date.getHours() + ':' + date.getMinutes();
}


const form = document.getElementsByClassName('elementor-form')[0];
form.addEventListener('submit', enviarDados);


function enviarDados() {


    // ENDPOINT
    let endpoint = "https://app.pipe.run/webservice/integradorJson?hash=aaaa0000-0a00-0a0a-0000-0aa000a00aa0"


    let dataHora = formatDate(new Date());
    let name = jQuery('#form-field-nome').val();
    let email = jQuery('#form-field-email').val();
    let company = jQuery('#form-field-empresa').val();
    let phone = jQuery('#form-field-phone').val();
    let job_title = jQuery('#form-field-cargo').val();
    let message = jQuery('#form-field-message').val();
    
    let utm_source = getParameterByName('utm_source');
    let utm_medium = getParameterByName('utm_medium');
    let utm_campaign = getParameterByName('utm_campaign');
    let utm_term = getParameterByName('utm_term');
    let utm_content = getParameterByName('utm_content');
    let utm_position = getParameterByName('utm_position');
    let utm_device = getParameterByName('utm_device');
    let utm_match = getParameterByName('utm_match');
    let utm_creative = getParameterByName('utm_creative');


    // RULES
    let rules = {
        "update": true,
        "status": "open",
        "equal_pipeline": true,
        "filter_status_update": "open",
    }


    // LEAD
    let lead = [{
        "id": email,
        "title": dataHora + " " + company,
        "name": name,
        "email": email,
        "company": company,
        "mobile_phone": phone,
        "job_title": job_title,
        "last_conversion": {
            "source" : utm_source || "Site CRM PipeRun"
        },
        "custom_fields": {
            "url_conversao": location.href,
            "utm_source": utm_source,
            "utm_medium": utm_medium,
            "utm_campaign": utm_campaign,
            "utm_term": utm_term,
            "utm_content": utm_content,
            "utm_position": utm_position,
            "utm_device": utm_device,
            "utm_match": utm_match,
            "utm_creative": utm_creative
        },
        "notes" : [
            "Título: " + dataHora + " Fale com Consultor CRM</br>" +
            "E-mail: " + email + "</br>" +
            "Nome: " + name + "</br>" +
            "WhatsApp: " + phone + "</br>" +
            "Empresa: "  + company + "</br>" +
            "Mensagem: "  + message + "</br>" +
            "utm_source: " + utm_source + "</br>" +
            "utm_medium: " + utm_medium + "</br>" +
            "utm_campaign: " + utm_campaign + "</br>" +
            "utm_term: " + utm_term + "</br>" +
            "utm_content: " + utm_content + "</br>" +
            "utm_position: " + utm_position + "</br>" +
            "utm_device: " + utm_device + "</br>" +
            "utm_match: " + utm_match + "</br>" +
            "utm_creative: " + utm_creative
        ]
    }]


    jQuery.each(lead[0], function(index, value) {
        if (typeof value == 'string' && value == '') {
            delete lead[0][index]
        }
    }); 


    // DATA
    let dataToSend = {
        "rules": rules,
        "leads": lead
    }
    jQuery.ajax({
        type: "post",
        data: JSON.stringify(dataToSend),
        dataType: "json",
        url: endpoint, 
        success: function(data) {
            ga('send', 'event', 'form', 'contato', 'captura_lead_corporativo');
        }
    });
}