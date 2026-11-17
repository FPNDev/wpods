import { Choice } from '../components/choiceList';
import { Note } from '../components/common';
import { createSwitchRow } from '../components/switchRow';
import { setAttr, setChecked, setClass, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import type { MixerDevice } from '../protocol';
import { store } from '../state';
import type { Ctx, Page } from './page';

type Item = {
  device: MixerDevice;
  row: HTMLButtonElement;
  name: Text;
  desc: HTMLSpanElement;
  volume: HTMLDivElement;
  input: HTMLInputElement;
  value: HTMLSpanElement;
};
type Filter = 'disconnected' | 'disabled';

// A picked device always shows, whatever the filters.
const filtered = (d: MixerDevice): boolean => {
  const m = store.mixer;
  if (d.on || d.state === 'active') {
    return false;
  }

  return d.state === 'disabled' ? m.hideDisabled : m.hideDisconnected;
};

// Moves rows only when the order changed, so the focused row keeps its focus.
const place = (list: HTMLElement, nodes: HTMLElement[]): void => {
  const children = list.children;
  if (children.length !== nodes.length || !nodes.every((node, i) => children[i] === node)) {
    list.replaceChildren(...nodes);
  }
};

const showVolume = (item: Item, volume: number): void => {
  item.input.style.setProperty('--v', `${volume}%`);
  setText(item.value, t('battery.pct', { pct: volume }));
};

export const createMixer = (ctx: Ctx): Page => {
  const items = new Map<string, Item>();
  const filter =
    (name: Filter) =>
    (show: boolean): void => {
      if (name === 'disconnected') {
        store.mixer.hideDisconnected = !show;
      } else {
        store.mixer.hideDisabled = !show;
      }
      post({ type: 'setMixerFilter', name, value: show });
      ctx.update('mixer');
    };
  const enabled = createSwitchRow({
    id: 'mixerEnabledTitle',
    title: 'mixer.enabled',
    desc: 'mixer.enabledDesc',
    onChange: (value) => {
      store.mixer.enabled = value;
      post({ type: 'setMixerEnabled', value });
      ctx.update('mixer');
    },
  });
  const showDisconnected = createSwitchRow({
    id: 'mixerDisconnectedTitle',
    title: 'mixer.showDisconnected',
    desc: null,
    onChange: filter('disconnected'),
  });
  const showDisabled = createSwitchRow({
    id: 'mixerDisabledTitle',
    title: 'mixer.showDisabled',
    desc: null,
    onChange: filter('disabled'),
  });
  setHidden(showDisconnected.desc, true);
  setHidden(showDisabled.desc, true);

  const create = (d: MixerDevice): Item => {
    const id = d.id;
    const name = document.createTextNode(d.name);
    const desc = (<span class="wp-row__desc" />) as HTMLSpanElement;
    let input!: HTMLInputElement;
    let value!: HTMLSpanElement;
    const row = Choice(
      'checkbox',
      name,
      () => {
        const device = item.device;
        device.on = !device.on;
        post({ type: 'setMixerSource', id, value: device.on });
        ctx.update('mixer');
      },
      desc,
    );
    const volume = (
      <div class="wp-row wp-mixer__volume">
        <div class="wp-slider">
          <input
            class="wp-slider__input"
            type="range"
            min="0"
            max="100"
            ref={input}
            oninput={() => {
              const v = Number(input.value);
              item.device.volume = v;
              showVolume(item, v);
              post({ type: 'previewMixerVolume', id, value: v });
            }}
            onchange={() => {
              post({ type: 'setMixerVolume', id, value: Number(input.value) });
            }}
          />
          <span class="wp-slider__value" ref={value} />
        </div>
      </div>
    ) as HTMLDivElement;
    const item: Item = { device: d, row, name, desc, volume, input, value };

    return item;
  };

  let inputGroup!: HTMLElement;
  let inputList!: HTMLDivElement;
  let outputGroup!: HTMLElement;
  let outputList!: HTMLDivElement;
  const el = (
    <div class="wp-page">
      {Note({ label: 'mixer.note' })}
      <section class="wp-group">
        <div class="wp-list">{enabled.row}</div>
      </section>
      <section class="wp-group">
        <div class="wp-list">
          {showDisconnected.row}
          {showDisabled.row}
        </div>
      </section>
      <section class="wp-group" ref={inputGroup}>
        <h2
          class="wp-group__title"
          id="mixerInputsTitle"
          l:text="mixer.inputs"
        />
        <div
          class="wp-list"
          role="group"
          aria-labelledby="mixerInputsTitle"
          ref={inputList}
        />
      </section>
      <section class="wp-group" ref={outputGroup}>
        <h2
          class="wp-group__title"
          id="mixerOutputsTitle"
          l:text="mixer.outputs"
        />
        <div
          class="wp-list"
          role="group"
          aria-labelledby="mixerOutputsTitle"
          ref={outputList}
        />
      </section>
    </div>
  ) as HTMLDivElement;

  const render = (): void => {
    const m = store.mixer;
    setChecked(enabled.input, m.enabled);
    // Off: the picks stay editable but look inactive, since nothing plays.
    setClass(inputGroup, 'wp-mixer--off', !m.enabled);
    setClass(outputGroup, 'wp-mixer--off', !m.enabled);
    setChecked(showDisconnected.input, !m.hideDisconnected);
    setChecked(showDisabled.input, !m.hideDisabled);
    const inputs: HTMLElement[] = [];
    const outputs: HTMLElement[] = [];
    const shown = new Set<string>();
    for (const d of m.devices) {
      if (filtered(d)) {
        continue;
      }
      shown.add(d.id);
      let item = items.get(d.id);
      if (!item) {
        item = create(d);
        items.set(d.id, item);
      }
      item.device = d;
      if (item.name.data !== d.name) {
        item.name.data = d.name;
      }
      const active = d.state === 'active';
      setAttr(item.row, 'aria-checked', String(d.on));
      setClass(item.row, 'wp-mixer__inactive', !active);
      setHidden(item.desc, active);
      if (!active) {
        setText(
          item.desc,
          t(d.state === 'disabled' ? 'mixer.disabled' : 'mixer.notConnected'),
        );
      }
      setHidden(item.volume, !d.on);
      setAttr(item.input, 'aria-label', t('mixer.volume', { name: d.name }));
      if (item.input.value !== String(d.volume)) {
        item.input.value = String(d.volume);
      }
      showVolume(item, d.volume);
      (d.flow === 'input' ? inputs : outputs).push(item.row, item.volume);
    }
    for (const id of items.keys()) {
      if (!shown.has(id)) {
        items.delete(id);
      }
    }
    place(inputList, inputs);
    place(outputList, outputs);
    setHidden(inputGroup, inputs.length === 0);
    setHidden(outputGroup, outputs.length === 0);
  };

  return { el, sections: { mixer: render } };
};
