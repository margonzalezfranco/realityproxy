# Reality Proxy: Fluid Interactions with Real-World Objects in MR via Abstract Representations

[![arXiv](https://img.shields.io/badge/arXiv-2507.17248-b31b1b.svg)](https://arxiv.org/abs/2507.17248) 
[![Conference](https://img.shields.io/badge/UIST-2025-blue)](https://dl.acm.org/doi/10.1145/3746059.3747709)

**Reality Proxy** is a system designed to decouple interaction from physical constraints in Mixed Reality (MR). By introducing abstract representations—or "proxies"—of real-world objects, the system enables users to interact with distant, occluded, or crowded objects through high-fidelity, semantic-rich digital twins placed within immediate reach.

---

## 🚀 Overview

Interacting with physical objects in MR is often hindered by their physical location, size, or arrangement. Reality Proxy shifts the interaction target from the physical object to a proxy. These proxies are:

* **Decoupled:** Placed in convenient locations for easy direct manipulation.
* **Enriched:** Infused with AI-derived semantic attributes and hierarchical spatial relationships.
* **Dynamic:** Generated on-the-fly using Computer Vision and LLMs to understand the scene structure.

## ✨ Key Features

* **Gaze+Pinch Proxy Creation:** Seamlessly transition from looking at a real-world object to interacting with its proxy.
* **Semantic Filtering:** Interact with objects based on attributes (e.g., "Select all monitors" or "Find the empty coffee cups").
* **Hierarchical Navigation:** Navigate nested groups of objects, such as items on a specific shelf or drones in a fleet.
* **Versatile Scenarios:** Applied to office information retrieval, large-scale spatial navigation, and multi-drone control.

## 🛠 Tech Stack

* **Hardware:** Developed and tested primarily on **Apple Vision Pro (AVP)**.
* **Interactions:** Gaze-based targeting combined with hand-gesture (pinch) manipulation.
* **Intelligence:** Integration of Computer Vision and Large Language Models (LLMs) for semantic scene understanding.

## 📖 Publication

If you use this work or find it helpful for your research, please cite the **UIST 2025** paper:

```bibtex
@inproceedings{liu2025realityproxy,
  title={Reality Proxy: Fluid Interactions with Real-World Objects in MR via Abstract Representations},
  author={Liu, Xiaoan and Jia, Difan and Liu, Xianhao Carton and Gonzalez-Franco, Mar and Chen, Zhu-Tian  },
  booktitle={Proceedings of the 38th Annual ACM Symposium on User Interface Software and Technology (UIST '25)},
  year={2025},
  doi={10.1145/3746059.3747709},
  url={[https://doi.org/10.1145/3746059.3747709](https://doi.org/10.1145/3746059.3747709)}
}
```

## 👥 Main Contributors

* Xiaoan Liu - University of Colorado
* Mar Gonzalez-Franco - Google
* Zhutian Cheng - University of Minnesota